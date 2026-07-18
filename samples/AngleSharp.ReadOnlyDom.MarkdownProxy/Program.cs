using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using AngleSharp.ReadOnlyDom.Streaming;

var builder = WebApplication.CreateBuilder(args);
builder
    .Services.AddHttpClient(
        "html",
        client => client.DefaultRequestHeaders.UserAgent.ParseAdd("RODOM-Markdown-Proxy/0.1")
    )
    .ConfigurePrimaryHttpMessageHandler(() =>
        new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }
    );

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet(
    "/demo.html",
    () =>
        Results.Content(
            "<html><head><title>RODOM demo</title></head><body><h1>Hello</h1>"
                + "<p>HTML bytes become <b>Markdown</b>, with <a href=\"https://example.com/\">working links</a>.</p>"
                + "<ul><li>Low allocation</li><li>Very incomplete</li></ul>"
                + "<table><thead><tr><th>Lane</th><th>Allocated</th></tr></thead><tbody>"
                + "<tr><td>DOM</td><td>megabytes</td></tr><tr><td>Fold</td><td>kilobytes</td></tr></tbody></table>"
                + "</body></html>",
            "text/html; charset=utf-8"
        )
);

app.MapGet(
    "/markdown",
    async Task (HttpContext context, IHttpClientFactory clients, string url, bool stream = true) =>
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("url must be an absolute http or https URI", context.RequestAborted);
            return;
        }

        using var upstream = await GetFollowingRedirectsAsync(
            clients.CreateClient("html"),
            uri,
            context.RequestAborted
        );
        upstream.EnsureSuccessStatusCode();
        context.Response.Headers["X-Source-Url"] = upstream.RequestMessage?.RequestUri?.AbsoluteUri ?? uri.AbsoluteUri;

        await using var source = await upstream.Content.ReadAsStreamAsync(context.RequestAborted);
        var reader = PipeReader.Create(source);
        try
        {
            if (stream)
            {
                context.Response.ContentType = "text/markdown; charset=utf-8";
                await MarkdownPlan.Instance.ExecuteBackpressuredAsync(
                    reader,
                    context.Response.BodyWriter,
                    new MarkdownBuffer(),
                    cancellationToken: context.RequestAborted
                );
            }
            else
            {
                var markdown = await MarkdownPlan.Instance.ExecuteAsync(
                    reader,
                    new MarkdownBuffer(),
                    context.RequestAborted
                );
                await WriteMarkdown(context, markdown.WrittenMemory);
            }
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }
);

app.MapPost(
    "/markdown",
    async Task (HttpContext context, bool stream = true) =>
    {
        if (stream)
        {
            context.Response.ContentType = "text/markdown; charset=utf-8";
            await MarkdownPlan.Instance.ExecuteBackpressuredAsync(
                context.Request.BodyReader,
                context.Response.BodyWriter,
                new MarkdownBuffer(),
                cancellationToken: context.RequestAborted
            );
        }
        else
        {
            var markdown = await MarkdownPlan.Instance.ExecuteAsync(
                context.Request.BodyReader,
                new MarkdownBuffer(),
                context.RequestAborted
            );
            await WriteMarkdown(context, markdown.WrittenMemory);
        }
    }
);

app.MapGet(
    "/text",
    async Task (HttpContext context, IHttpClientFactory clients, string url, bool stream = true) =>
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("url must be an absolute http or https URI", context.RequestAborted);
            return;
        }

        using var upstream = await GetFollowingRedirectsAsync(
            clients.CreateClient("html"),
            uri,
            context.RequestAborted
        );
        upstream.EnsureSuccessStatusCode();
        context.Response.Headers["X-Source-Url"] = upstream.RequestMessage?.RequestUri?.AbsoluteUri ?? uri.AbsoluteUri;

        await using var source = await upstream.Content.ReadAsStreamAsync(context.RequestAborted);
        var reader = PipeReader.Create(source);
        try
        {
            if (stream)
            {
                context.Response.ContentType = "text/plain; charset=utf-8";
                await HtmlTextExtractor.Default.ExtractAsync(
                    reader,
                    context.Response.BodyWriter,
                    cancellationToken: context.RequestAborted
                );
            }
            else
            {
                var text = new ArrayBufferWriter<byte>();
                await HtmlTextExtractor.Default.ExtractAsync(reader, text, context.RequestAborted);
                await WriteUtf8(context, text.WrittenMemory, "text/plain; charset=utf-8");
            }
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }
);

app.MapPost(
    "/text",
    async Task (HttpContext context, bool stream = true) =>
    {
        if (stream)
        {
            context.Response.ContentType = "text/plain; charset=utf-8";
            await HtmlTextExtractor.Default.ExtractAsync(
                context.Request.BodyReader,
                context.Response.BodyWriter,
                cancellationToken: context.RequestAborted
            );
        }
        else
        {
            var text = new ArrayBufferWriter<byte>();
            await HtmlTextExtractor.Default.ExtractAsync(context.Request.BodyReader, text, context.RequestAborted);
            await WriteUtf8(context, text.WrittenMemory, "text/plain; charset=utf-8");
        }
    }
);

app.MapGet(
    "/asset",
    async Task (HttpContext context, IHttpClientFactory clients, string url) =>
    {
        const long maximumKnownLength = 20 * 1024 * 1024;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var upstream = await GetFollowingRedirectsAsync(
            clients.CreateClient("html"),
            uri,
            context.RequestAborted
        );
        upstream.EnsureSuccessStatusCode();
        var mediaType = upstream.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }
        if (upstream.Content.Headers.ContentLength is > maximumKnownLength)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        context.Response.ContentType = upstream.Content.Headers.ContentType!.ToString();
        context.Response.Headers.CacheControl = "public, max-age=300";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        await upstream.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
);

app.Run();

static async ValueTask WriteMarkdown(HttpContext context, ReadOnlyMemory<byte> markdown)
{
    await WriteUtf8(context, markdown, "text/markdown; charset=utf-8");
}

static async ValueTask WriteUtf8(HttpContext context, ReadOnlyMemory<byte> value, string contentType)
{
    context.Response.ContentType = contentType;
    context.Response.ContentLength = value.Length;
    await context.Response.BodyWriter.WriteAsync(value, context.RequestAborted);
}

static async ValueTask<HttpResponseMessage> GetFollowingRedirectsAsync(
    HttpClient client,
    Uri initialUri,
    CancellationToken cancellationToken
)
{
    const int maximumRedirects = 10;
    var uri = initialUri;
    for (var redirect = 0; redirect <= maximumRedirects; redirect++)
    {
        var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!IsRedirect(response.StatusCode))
            return response;

        var location = response.Headers.Location;
        if (location is null)
            return response;
        if (redirect == maximumRedirects)
        {
            response.Dispose();
            throw new HttpRequestException($"The response exceeded {maximumRedirects} redirects.");
        }

        var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
        response.Dispose();
        if (next.Scheme is not ("http" or "https"))
            throw new HttpRequestException($"Redirected to unsupported URI scheme '{next.Scheme}'.");
        uri = next;
    }

    throw new InvalidOperationException("Unreachable redirect loop.");
}

static bool IsRedirect(HttpStatusCode statusCode) =>
    statusCode
        is HttpStatusCode.Moved
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
