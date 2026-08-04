using System.Buffers;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming.Query;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var port = int.Parse(Environment.GetEnvironmentVariable("BENCHMARK_PORT") ?? "5081");
var query = CreateUrlQuery();
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

Console.WriteLine($"READY http://127.0.0.1:{port}");
await app.RunAsync();

static QueryPlan<UrlState> CreateUrlQuery()
{
    var list = StreamQuery.For<UrlState>("ul").Class("news-list");
    var card = list.Descendant("li").Attribute("dt-eid", "em_item_article");
    card.Descendant("a")
        .Attribute("href")
        .OnStart(static (ref state, in element) => state.Add(element), "href");
    return list.Compile();
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
