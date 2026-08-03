using AngleSharp.ReadOnlyDom.MarkdownProxy.MD;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.ReadOnlyDom.Streaming.Public;
using AngleSharp.ReadOnlyDom.Streaming.Utf8Tokenizer;
using AngleSharp.Streaming.Utf8;

const long MaximumInputBytes = 4L * 1024 * 1024;
var limits = new HtmlStreamingLimits(maximumInputBytes: MaximumInputBytes);
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost(
    "/markdown",
    async Task (HttpContext context, bool stream = true) =>
    {
        if (context.Request.ContentLength is > MaximumInputBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        if (stream)
        {
            context.Response.ContentType = "text/markdown; charset=utf-8";
            await MarkdownPlan.Instance.ExecuteBackpressuredAsync(
                context.Request.BodyReader,
                context.Response.BodyWriter,
                new MarkdownBuffer(),
                cancellationToken: context.RequestAborted,
                limits: limits
            );
            return;
        }

        var markdown = await MarkdownPlan.Instance.ExecuteAsync(
            context.Request.BodyReader,
            new MarkdownBuffer(),
            context.RequestAborted,
            limits
        );
        context.Response.ContentType = "text/markdown; charset=utf-8";
        context.Response.ContentLength = markdown.WrittenMemory.Length;
        await context.Response.BodyWriter.WriteAsync(markdown.WrittenMemory, context.RequestAborted);
    }
);

app.Run();
