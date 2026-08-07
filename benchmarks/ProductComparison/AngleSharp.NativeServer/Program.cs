using System.Buffers;
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
var rewriteLimits =
    Environment.GetEnvironmentVariable("BENCHMARK_UNLIMITED") == "1" ? HtmlStreamingLimits.Unlimited : null;
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
        var state = await query.ExecuteAsync(context.Request.BodyReader, new UrlState(), context.RequestAborted);
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
        // Full-duplex streaming (#61): rewritten output leaves through the response while the
        // request body is still arriving. Only the currently open start tag is ever buffered, so
        // peak memory is independent of document size - the same profile as the lol-html lane.
        context.Response.ContentType = "text/html; charset=utf-8";
        var reader = context.Request.BodyReader;
        var writer = context.Response.BodyWriter;
        using var session = rewriteQuery.CreateRewriteSession(
            new RewriteState(),
            writer,
            static (ref RewriteState state, in Element _, ref StartTagEditor tag) =>
            {
                state.Count++;
                tag.AppendAttribute("data-q"u8, "1"u8);
            },
            Utf8InputContract.ArbitraryBytes,
            rewriteLimits
        );
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(context.RequestAborted);
                foreach (var segment in read.Buffer)
                    session.Write(segment.Span);
                reader.AdvanceTo(read.Buffer.End);
                if (!writer.CanGetUnflushedBytes || writer.UnflushedBytes >= 16 * 1024)
                    await writer.FlushAsync(context.RequestAborted);
                if (read.IsCompleted)
                    break;
            }
            session.Complete();
        }
        catch (HtmlStreamingLimitExceededException)
        {
            if (!context.Response.HasStarted && (!writer.CanGetUnflushedBytes || writer.UnflushedBytes == 0))
            {
                // The status line is still ours to change.
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
            // Output already left (or sits in the pipe); the only honest signal is a broken stream.
            context.Abort();
            return;
        }
        await writer.FlushAsync(context.RequestAborted);
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
    card.Descendant("a").Attribute("href").OnStart(static (ref state, in element) => state.Add(element), "href");
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
