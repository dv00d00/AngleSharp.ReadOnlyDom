using System.IO.Pipelines;
using System.Text;
using AngleSharp.ReadOnlyDom.HackerNews.Feed;
using AngleSharp.ReadOnlyDom.HackerNews.Preview;
using AngleSharp.ReadOnlyDom.HackerNews.Upstream;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.ReadOnlyDom.Streaming.Input;
using AngleSharp.ReadOnlyDom.Streaming.Output;

// Hacker News pages are small and well-formed; an arbitrary linked page is neither, so it gets a tighter
// budget and a page-sized ceiling instead of the default 128 MiB.
var feedLimits = new HtmlStreamingLimits(maximumInputBytes: 4L * 1024 * 1024);
var previewLimits = new HtmlStreamingLimits(maximumInputBytes: 8L * 1024 * 1024);
var feedLifetime = TimeSpan.FromSeconds(15);
var previewLifetime = TimeSpan.FromMinutes(10);
const long MaximumImageBytes = 5L * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(_ => new UpstreamFetcher());
builder.Services.AddSingleton(_ => new SnapshotCache());
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Hacker News list markup -> one NDJSON line per story, published as each story's subtext row closes.
app.MapGet(
    "/api/stories",
    async Task (
        HttpContext context,
        UpstreamFetcher upstream,
        SnapshotCache cache,
        ILoggerFactory loggerFactory,
        String feed = "news"
    ) =>
    {
        if (!HackerNewsFeeds.TryResolve(feed, out var name, out var source))
        {
            await Results.BadRequest($"Unknown feed '{feed}'.").ExecuteAsync(context);
            return;
        }

        context.Response.Headers.CacheControl = "no-store";

        if (cache.TryGet($"feed:{name}", feedLifetime, out var snapshot, out var age))
        {
            context.Response.ContentType = "application/x-ndjson; charset=utf-8";
            context.Response.ContentLength = snapshot.Length;
            context.Response.Headers["X-Snapshot-Age"] = ((Int32)age.TotalSeconds).ToString();
            await context.Response.BodyWriter.WriteAsync(snapshot, context.RequestAborted);
            return;
        }

        var fetched = await FetchAsync(context, upstream, source);
        if (fetched is null)
            return;

        using var response = fetched;
        if (!response.IsSuccessStatusCode)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsync($"Hacker News answered {(Int32)response.StatusCode}.");
            return;
        }

        await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        context.Response.ContentType = "application/x-ndjson; charset=utf-8";
        context.Response.Headers["X-Snapshot-Age"] = "0";

        using var stories = new StoryFeedBuffer();
        try
        {
            await StoryFeedPlan.Instance.ExecuteBackpressuredAsync(
                PipeReader.Create(body),
                context.Response.BodyWriter,
                stories,
                // Publish a record the moment it is final rather than waiting for a 16 KiB batch: the
                // point of the sample is that the first row renders before the last byte arrives.
                flushThreshold: 1,
                inputSliceSize: 4 * 1024,
                cancellationToken: context.RequestAborted,
                limits: feedLimits
            );
        }
        catch (HtmlStreamingLimitExceededException exception)
        {
            loggerFactory.CreateLogger("stories").LogWarning(exception, "The {Feed} feed exceeded its limits.", name);
            return;
        }

        cache.Store($"feed:{name}", stories.Transcript);
    }
);

// A linked page -> the NDJSON of one link-preview card, published field by field and abandoned as soon as
// the head ends.
app.MapGet(
    "/api/preview",
    async Task (
        HttpContext context,
        UpstreamFetcher upstream,
        SnapshotCache cache,
        ILoggerFactory loggerFactory,
        String? url
    ) =>
    {
        if (!UpstreamFetcher.TryParseTarget(url, out var target, out var error))
        {
            await Results.BadRequest(error).ExecuteAsync(context);
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

        var key = $"preview:{target.AbsoluteUri}";
        if (cache.TryGet(key, previewLifetime, out var cached, out var cachedAge))
        {
            context.Response.ContentType = "application/x-ndjson; charset=utf-8";
            context.Response.ContentLength = cached.Length;
            context.Response.Headers["X-Snapshot-Age"] = ((Int32)cachedAge.TotalSeconds).ToString();
            await context.Response.BodyWriter.WriteAsync(cached, context.RequestAborted);
            return;
        }

        var fetched = await FetchAsync(context, upstream, target);
        if (fetched is null)
            return;

        using var response = fetched;
        if (!response.IsSuccessStatusCode)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsync($"{target.Host} answered {(Int32)response.StatusCode}.");
            return;
        }

        if (!UpstreamFetcher.IsHtml(response.Content.Headers))
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            await context.Response.WriteAsync($"{response.Content.Headers.ContentType} is not an HTML document.");
            return;
        }

        // Everything a card needs is in the head, so the input ends the moment the head does. The rest of
        // the document — usually the overwhelming majority of it — is never pulled off the wire.
        await using var body = new EarlyStopStream(await response.Content.ReadAsStreamAsync(context.RequestAborted));
        context.Response.ContentType = "application/x-ndjson; charset=utf-8";
        context.Response.Headers["X-Snapshot-Age"] = "0";

        // Relative URLs in the document resolve against where the response actually came from, which is
        // not necessarily where the request was sent.
        using var preview = new PreviewBuffer(response.RequestMessage?.RequestUri ?? target, body.StopReading);
        try
        {
            await PreviewPlan.Instance.ExecuteEncodedBackpressuredAsync(
                PipeReader.Create(body),
                context.Response.BodyWriter,
                ResolveEncoding(UpstreamFetcher.ReadCharset(response.Content.Headers)),
                preview,
                flushThreshold: 1,
                inputSliceSize: 4 * 1024,
                cancellationToken: context.RequestAborted,
                limits: previewLimits
            );
        }
        catch (HtmlStreamingLimitExceededException exception)
        {
            loggerFactory.CreateLogger("preview").LogWarning(exception, "{Target} exceeded its limits.", target);
        }

        // BytesRead counts decoded bytes: with content-encoding in play there is no honest comparison
        // against a declared length, so the record reports what was read and whether reading was cut short.
        var stats = Encoding.UTF8.GetBytes(
            $$"""{"kind":"stats","bytesRead":{{body.BytesRead}},"stopped":{{(body.Stopped ? "true" : "false")}}}"""
                + "\n"
        );
        await context.Response.BodyWriter.WriteAsync(stats, context.RequestAborted);

        var transcript = new byte[preview.Transcript.Length + stats.Length];
        preview.Transcript.Span.CopyTo(transcript);
        stats.CopyTo(transcript, preview.Transcript.Length);
        cache.Store(key, transcript);
    }
);

// Images referenced by a preview are fetched through the same outbound boundary and re-served from this
// origin, so opening a preview never dials a third party from the browser.
app.MapGet(
    "/api/image",
    async Task (HttpContext context, UpstreamFetcher upstream, String? url) =>
    {
        if (!UpstreamFetcher.TryParseTarget(url, out var target, out var error))
        {
            await Results.BadRequest(error).ExecuteAsync(context);
            return;
        }

        var fetched = await FetchAsync(context, upstream, target);
        if (fetched is null)
            return;

        using var response = fetched;
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (
            !response.IsSuccessStatusCode
            || mediaType is null
            || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        )
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        context.Response.ContentType = mediaType;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.CacheControl = "private, max-age=300";

        await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        var writer = context.Response.BodyWriter;
        var remaining = MaximumImageBytes;
        while (remaining > 0)
        {
            var destination = writer.GetMemory(16 * 1024);
            var window = destination[..(Int32)Math.Min(destination.Length, remaining)];
            var read = await body.ReadAsync(window, context.RequestAborted);
            if (read == 0)
                break;

            writer.Advance(read);
            remaining -= read;
            await writer.FlushAsync(context.RequestAborted);
        }
    }
);

app.Run();
return;

// Fetches upstream headers before a single response byte is written, so a refused target or a dead host
// still gets a status code instead of a truncated stream.
static async Task<HttpResponseMessage?> FetchAsync(HttpContext context, UpstreamFetcher upstream, Uri target)
{
    try
    {
        return await upstream.GetAsync(target, context.RequestAborted);
    }
    catch (Exception exception) when (exception is HttpRequestException or UpstreamBlockedException or IOException)
    {
        var (status, message) = UpstreamFetcher.Describe(exception);
        context.Response.StatusCode = status;
        await context.Response.WriteAsync(message);
        return null;
    }
    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
    {
        context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
        await context.Response.WriteAsync($"{target.Host} did not answer in time.");
        return null;
    }
}

// The transport's charset wins when it names an encoding the runtime knows; otherwise the document's own
// BOM or meta declaration decides, and Windows-1252 is the legacy fallback.
static HtmlInputEncoding ResolveEncoding(String? charset)
{
    if (String.IsNullOrWhiteSpace(charset))
        return HtmlInputEncoding.Auto();

    try
    {
        return HtmlInputEncoding.Known(Encoding.GetEncoding(charset));
    }
    catch (ArgumentException)
    {
        return HtmlInputEncoding.Auto();
    }
}
