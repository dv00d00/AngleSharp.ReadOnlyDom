using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming.Utf8Stream;

var builder = WebApplication.CreateBuilder(args);
builder
    .Services.AddHttpClient(
        "html",
        client => client.DefaultRequestHeaders.UserAgent.ParseAdd("RODOM-Markdown-Proxy/0.1")
    )
    .ConfigurePrimaryHttpMessageHandler(() =>
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        }
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
    statusCode is HttpStatusCode.Moved
        or HttpStatusCode.Redirect
        or HttpStatusCode.RedirectMethod
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

internal static class MarkdownPlan
{
    internal static readonly QueryPlan<MarkdownBuffer> Instance = Create();

    private static QueryPlan<MarkdownBuffer> Create()
    {
        var html = StreamQuery
            .For<MarkdownBuffer>("html")
            .OnText(static (ref output, text) => output.AppendInlineText(text))
            .OnEnd(static (ref output) => output.CompleteDocument());
        html.Descendant("title")
            .OnNormalizedText(static (ref output, in element) => output.DocumentTitle(element.TextUtf8));
        html.Descendant("article")
            .OnStart(static (ref output, in _) => output.StartPreferredArticle())
            .OnEnd(static (ref output) => output.EndPreferredArticle());
        html.Descendant("h1").AsInlineBlock("# "u8);
        html.Descendant("h2").AsInlineBlock("## "u8);
        html.Descendant("h3").AsInlineBlock("### "u8);
        html.Descendant("h4").AsInlineBlock("#### "u8);
        html.Descendant("h5").AsInlineBlock("##### "u8);
        html.Descendant("h6").AsInlineBlock("###### "u8);
        html.Descendant("p").AsInlineBlock();
        html.Descendant("li").AsInlineBlock("- "u8);
        html.Descendant("blockquote").AsInlineBlock("> "u8);
        html.Descendant("a").WithAttribute("href").AsInlineLink();
        html.Descendant("pre").OnTextContent(static (ref output, in element) => output.FencedCode(element.TextUtf8));
        html.Descendant("hr").OnClose(static (ref output, in _) => output.Block("---"u8, default));
        html.Descendant("img")
            .OnClose(
                static (ref output, in element) =>
                {
                    if (element.TryGetAttributeUtf8("src"u8, out var source))
                    {
                        if (source.StartsWith("data:"u8))
                            return;
                        element.TryGetAttributeUtf8("alt"u8, out var alt);
                        output.Image(alt, source);
                    }
                },
                "src",
                "alt"
            );

        html.Descendant("table")
            .WithId("hnmain")
            .OnStart(static (ref output, in _) => output.StartLayoutTable())
            .OnEnd(static (ref output) => output.EndLayoutTable());
        html.Descendant("tr").WithClass("athing").AsInlineBlock("- "u8);
        html.Descendant("span").WithClass("subline").AsInlineBlock("  "u8);

        var table = html.Descendant("table")
            .OnStart(static (ref output, in _) => output.StartTable())
            .OnEnd(static (ref output) => output.EndTable());
        var row = table
            .Descendant("tr")
            .OnStart(static (ref output, in _) => output.StartRow())
            .OnEnd(static (ref output) => output.EndRow());
        row.Child("th").OnNormalizedText(static (ref output, in element) => output.Cell(element.TextUtf8));
        row.Child("td").OnNormalizedText(static (ref output, in element) => output.Cell(element.TextUtf8));
        return html.Compile();
    }
}

internal static class MarkdownQueryExtensions
{
    internal static QueryNode<MarkdownBuffer> AsInlineBlock(
        this QueryNode<MarkdownBuffer> node,
        ReadOnlySpan<byte> prefix = default
    )
    {
        // Query plans live for the lifetime of the application; copy the compile-time span once.
        var ownedPrefix = prefix.ToArray();
        return node.OnStart((ref MarkdownBuffer output, in Element _) => output.StartInlineBlock(ownedPrefix))
            .OnEnd(static (ref output) => output.EndInlineBlock());
    }

    internal static QueryNode<MarkdownBuffer> AsInlineLink(this QueryNode<MarkdownBuffer> node) =>
        node.OnStart(
                static (ref output, in element) =>
                {
                    if (element.TryGetAttribute("href"u8, out var href))
                        output.StartInlineLink(href);
                },
                "href"
            )
            .OnEnd(static (ref output) => output.EndInlineLink());
}

internal sealed class MarkdownBuffer : ICommittedUtf8Output
{
    private readonly CommittedUtf8Buffer _output = new(4 * 1024);
    private readonly ArrayBufferWriter<byte> _row = new(256);
    private readonly ArrayBufferWriter<byte> _linkTarget = new(128);
    private readonly ArrayBufferWriter<byte> _documentTitle = new(128);
    private readonly ArrayBufferWriter<byte> _inlinePrefix = new(16);
    private int _tableDepth;
    private int _rowCells;
    private int _inlineBlockDepth;
    private int _preferredArticleDepth;
    private int _layoutTableDepth;
    private bool _firstTableRow;
    private bool _inlineLink;
    private bool _inlineLinkHasContent;
    private bool _inlineBlockHasContent;
    private bool _spaceBeforeInlineLink;
    private bool _pendingInlineSpace;
    private bool _preferredArticleFound;
    private bool _preferredArticleComplete;

    internal ReadOnlyMemory<byte> WrittenMemory => _output.WrittenUtf8;

    public ReadOnlyMemory<byte> CommittedUtf8 => _output.CommittedUtf8;

    public void AdvanceCommitted(int bytes) => _output.AdvanceCommitted(bytes);

    private bool AcceptsContent => !_preferredArticleFound || !_preferredArticleComplete && _preferredArticleDepth != 0;

    internal void DocumentTitle(ReadOnlySpan<byte> title)
    {
        _documentTitle.Clear();
        Write(_documentTitle, title);
        Block("# "u8, title);
    }

    internal void StartPreferredArticle()
    {
        if (_preferredArticleFound)
        {
            if (!_preferredArticleComplete && _preferredArticleDepth != 0)
                _preferredArticleDepth++;
            return;
        }

        _preferredArticleFound = true;
        _preferredArticleDepth = 1;
        _output.Clear();
        _tableDepth = 0;
        _inlineBlockDepth = 0;
        _inlineLink = false;
        _pendingInlineSpace = false;
        if (!_documentTitle.WrittenSpan.IsEmpty)
            Block("# "u8, _documentTitle.WrittenSpan);
    }

    internal void EndPreferredArticle()
    {
        if (!_preferredArticleFound || _preferredArticleComplete || _preferredArticleDepth == 0)
            return;
        _preferredArticleDepth--;
        if (_preferredArticleDepth == 0)
        {
            _output.Commit();
            _preferredArticleComplete = true;
        }
    }

    internal void CompleteDocument()
    {
        if (!_preferredArticleFound)
            _output.Commit();
    }

    internal void Block(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> text)
    {
        if (!AcceptsContent || _tableDepth != 0 || prefix.IsEmpty && text.IsEmpty)
            return;
        Write(prefix);
        Write(text);
        Write("\n\n"u8);
        CommitIfSafe();
    }

    internal void StartInlineBlock(ReadOnlySpan<byte> prefix)
    {
        if (!AcceptsContent || _tableDepth != 0)
            return;
        if (_inlineBlockDepth == 0)
        {
            _inlinePrefix.Clear();
            Write(_inlinePrefix, prefix);
            _inlineBlockHasContent = false;
            _pendingInlineSpace = false;
        }
        _inlineBlockDepth++;
    }

    internal void AppendInlineText(ReadOnlySpan<byte> utf8)
    {
        if (_inlineBlockDepth == 0 || _tableDepth != 0)
            return;
        while (!utf8.IsEmpty)
        {
            var status = Rune.DecodeFromUtf8(utf8, out var rune, out var consumed);
            if (status != OperationStatus.Done)
                throw new InvalidOperationException("The tokenizer emitted incomplete UTF-8 text.");
            var scalar = utf8[..consumed];
            utf8 = utf8[consumed..];
            if (Rune.IsWhiteSpace(rune))
            {
                _pendingInlineSpace = true;
                continue;
            }
            EnsureInlineBlockStarted();
            EnsureInlineLinkStarted();
            FlushInlineSpace();
            Write(scalar);
        }
    }

    internal void EndInlineBlock()
    {
        if (_inlineBlockDepth == 0 || _tableDepth != 0)
            return;
        _inlineBlockDepth--;
        _pendingInlineSpace = false;
        if (_inlineBlockDepth == 0 && _inlineBlockHasContent)
        {
            Write("\n\n"u8);
            CommitIfSafe();
        }
    }

    internal void StartInlineLink(ReadOnlySpan<byte> href)
    {
        if (_inlineBlockDepth == 0 || _tableDepth != 0 || _inlineLink)
            return;
        _linkTarget.Clear();
        Write(_linkTarget, href);
        _spaceBeforeInlineLink = _pendingInlineSpace;
        _pendingInlineSpace = false;
        _inlineLink = true;
        _inlineLinkHasContent = false;
    }

    internal void EndInlineLink()
    {
        if (!_inlineLink)
            return;
        if (!_inlineLinkHasContent)
        {
            _pendingInlineSpace |= _spaceBeforeInlineLink;
            _inlineLink = false;
            return;
        }
        _pendingInlineSpace = false;
        Write("]("u8);
        Write(_linkTarget.WrittenSpan);
        Write(")"u8);
        _inlineLink = false;
    }

    internal void FencedCode(ReadOnlySpan<byte> text)
    {
        if (!AcceptsContent || _tableDepth != 0 || text.IsEmpty)
            return;
        Write("```\n"u8);
        Write(text);
        if (text[^1] != (byte)'\n')
            Write("\n"u8);
        Write("```\n\n"u8);
        CommitIfSafe();
    }

    internal void Image(ReadOnlySpan<byte> alt, ReadOnlySpan<byte> source)
    {
        if (!AcceptsContent || _tableDepth != 0)
            return;
        Write("!["u8);
        Write(alt);
        Write("]("u8);
        Write(source);
        Write(")\n\n"u8);
        CommitIfSafe();
    }

    internal void StartLayoutTable()
    {
        _layoutTableDepth++;
        _tableDepth = 0;
        _row.Clear();
        _rowCells = 0;
    }

    internal void EndLayoutTable()
    {
        if (_layoutTableDepth != 0)
            _layoutTableDepth--;
    }

    internal void StartTable()
    {
        if (!AcceptsContent || _layoutTableDepth != 0)
            return;
        _tableDepth++;
        if (_tableDepth == 1)
            _firstTableRow = true;
    }

    internal void EndTable()
    {
        if (_layoutTableDepth != 0 || _tableDepth == 0)
            return;
        _tableDepth--;
        if (_tableDepth == 0)
        {
            Write("\n"u8);
            CommitIfSafe();
        }
    }

    internal void StartRow()
    {
        if (_tableDepth != 1)
            return;
        _row.Clear();
        _rowCells = 0;
    }

    internal void Cell(ReadOnlySpan<byte> text)
    {
        if (_tableDepth != 1)
            return;
        Write(_row, "| "u8);
        WriteEscapedCell(text);
        Write(_row, " "u8);
        _rowCells++;
    }

    internal void EndRow()
    {
        if (_tableDepth != 1 || _rowCells == 0)
            return;
        Write(_row, "|\n"u8);
        Write(_row.WrittenSpan);
        if (_firstTableRow)
        {
            for (var cell = 0; cell < _rowCells; cell++)
                Write("| --- "u8);
            Write("|\n"u8);
            _firstTableRow = false;
        }
        CommitIfSafe();
    }

    private void WriteEscapedCell(ReadOnlySpan<byte> value)
    {
        foreach (var character in value)
        {
            if (character == (byte)'|')
                Write(_row, "\\"u8);
            Write(_row, [character]);
        }
    }

    private void Write(ReadOnlySpan<byte> value)
    {
        Write(_output, value);
    }

    private void FlushInlineSpace()
    {
        if (!_pendingInlineSpace)
            return;
        Write(" "u8);
        _pendingInlineSpace = false;
    }

    private void EnsureInlineBlockStarted()
    {
        if (_inlineBlockHasContent)
            return;
        Write(_inlinePrefix.WrittenSpan);
        _inlineBlockHasContent = true;
    }

    private void EnsureInlineLinkStarted()
    {
        if (!_inlineLink || _inlineLinkHasContent)
            return;
        _pendingInlineSpace = _spaceBeforeInlineLink;
        FlushInlineSpace();
        Write("["u8);
        _inlineLinkHasContent = true;
    }

    private void CommitIfSafe()
    {
        if (_preferredArticleFound && !_preferredArticleComplete && _preferredArticleDepth != 0)
            _output.Commit();
    }

    private static void Write(IBufferWriter<byte> output, ReadOnlySpan<byte> value)
    {
        var destination = output.GetSpan(value.Length);
        value.CopyTo(destination);
        output.Advance(value.Length);
    }
}
