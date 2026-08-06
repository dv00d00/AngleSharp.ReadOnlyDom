using System.Buffers;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.ReadOnlyDom.Streaming.Query;
using AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var port = int.Parse(Environment.GetEnvironmentVariable("BENCHMARK_PORT") ?? "5081");
var query = CreateUrlQuery();
var rewriteQuery = CreateRewriteQuery();
// Default bounded limits are the realistic serving posture; the unlimited switch exists for
// apples-to-apples lanes against lol-html, which performs no resource accounting.
var rewriteLimits = Environment.GetEnvironmentVariable("BENCHMARK_UNLIMITED") == "1"
    ? HtmlStreamingLimits.Unlimited
    : null;
var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.ListenLocalhost(port, listen => listen.Protocols = HttpProtocols.Http1);
});

var app = builder.Build();
app.MapGet("/health", static () => Results.Text("ok"));
app.MapPost(
    "/extract",
    async context =>
    {
        var state = await query.ExecuteAsync(
            context.Request.BodyReader,
            new UrlState(),
            context.RequestAborted
        );
        var output = new ArrayBufferWriter<byte>(state.OutputLength);
        foreach (var url in state.Urls)
        {
            output.Write(url);
            output.GetSpan(1)[0] = (byte)'\n';
            output.Advance(1);
        }

        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength = output.WrittenCount;
        await context.Response.BodyWriter.WriteAsync(output.WrittenMemory, context.RequestAborted);
    }
);

app.MapPost(
    "/rewrite",
    async context =>
    {
        // Streaming-input rewrite is tracked by #61; until it lands the request buffers fully,
        // then the rewritten document publishes into the response pipe as borrowed segments
        // (Response.BodyWriter is the IBufferWriter the rewriter fills directly).
        var hint = (int)Math.Clamp(context.Request.ContentLength ?? 64 * 1024, 4096, 32 * 1024 * 1024);
        var input = new ArrayBufferWriter<byte>(hint);
        var reader = context.Request.BodyReader;
        while (true)
        {
            var read = await reader.ReadAsync(context.RequestAborted);
            foreach (var segment in read.Buffer)
                input.Write(segment.Span);
            reader.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
                break;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        try
        {
            rewriteQuery.Rewrite(
                input.WrittenSpan,
                context.Response.BodyWriter,
                new RewriteState(),
                static (ref RewriteState state, in Element _, ref StartTagEditor tag) =>
                {
                    state.Count++;
                    tag.AppendAttribute("data-q"u8, "1"u8);
                },
                Utf8InputContract.ArbitraryBytes,
                rewriteLimits
            );
        }
        catch (HtmlStreamingLimitExceededException)
        {
            // Nothing has been flushed yet, so the status line is still ours to change.
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        await context.Response.BodyWriter.FlushAsync(context.RequestAborted);
    }
);

Console.WriteLine($"READY http://127.0.0.1:{port}");
await app.RunAsync();

static QueryPlan<RewriteState> CreateRewriteQuery()
{
    // Same selector and edit as the console rewrite workload, so HTTP-lane and in-process
    // numbers describe the same work.
    var list = StreamQuery.For<RewriteState>("ul").Class("news-list");
    var card = list.Descendant("li").Attribute("dt-eid", "em_item_article");
    card.Descendant("a").Attribute("href");
    return list.Compile();
}

static QueryPlan<UrlState> CreateUrlQuery()
{
    var list = StreamQuery.For<UrlState>("ul").Class("news-list");
    var card = list.Descendant("li").Attribute("dt-eid", "em_item_article");
    card.Descendant("a")
        .Attribute("href")
        .OnStart(static (ref state, in element) => state.Add(element), "href");
    return list.Compile();
}

sealed class RewriteState
{
    public int Count;
}

sealed class UrlState
{
    public List<byte[]> Urls { get; } = [];
    public int OutputLength { get; private set; }

    public void Add(in Element element)
    {
        if (!element.TryGetAttribute("href", out var value))
            return;

        var owned = value.ToArray();
        Urls.Add(owned);
        OutputLength += owned.Length + 1;
    }
}
