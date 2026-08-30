using System.IO.Pipelines;
// The markdown projection types are linked in from the MarkdownProxy sample.
using AngleSharp.ReadOnlyDom.MarkdownProxy.MD;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.ReadOnlyDom.Streaming.Output;

const long MaximumPageBytes = 1024 * 1024;
var pages = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["/pages/index.html"] = "index.html",
    ["/pages/guide.html"] = "guide.html",
    ["/pages/architecture.html"] = "architecture.html",
};
var limits = new HtmlStreamingLimits(maximumInputBytes: MaximumPageBytes);
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.Use(
    async (context, next) =>
    {
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        await next();
    }
);
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet(
    "/markdown",
    async Task (HttpContext context, string page = "/pages/index.html") =>
    {
        if (!pages.TryGetValue(page, out var fileName))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var filePath = Path.Combine(app.Environment.ContentRootPath, "Pages", fileName);
        await using var input = File.OpenRead(filePath);
        var reader = PipeReader.Create(input);
        try
        {
            context.Response.ContentType = "text/markdown; charset=utf-8";
            context.Response.Headers["X-Source-Page"] = page;
            await MarkdownPlan.Instance.ExecuteBackpressuredAsync(
                reader,
                context.Response.BodyWriter,
                new MarkdownBuffer(),
                cancellationToken: context.RequestAborted,
                limits: limits
            );
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }
);

app.Run();
