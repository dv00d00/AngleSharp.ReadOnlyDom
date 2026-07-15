using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming.Utf8Stream;

var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddHttpClient("html", client => client.DefaultRequestHeaders.UserAgent.ParseAdd("RODOM-Markdown-Proxy/0.1"))
    .ConfigurePrimaryHttpMessageHandler(() =>
        new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }
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
    async (HttpContext context, IHttpClientFactory clients, string url, bool stream = false) =>
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("url must be an absolute http or https URI", context.RequestAborted);
            return;
        }

        using var upstream = await clients
            .CreateClient("html")
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        upstream.EnsureSuccessStatusCode();

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
    async (HttpContext context, bool stream = false) =>
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

app.Run();

static async ValueTask WriteMarkdown(HttpContext context, ReadOnlyMemory<byte> markdown)
{
    context.Response.ContentType = "text/markdown; charset=utf-8";
    context.Response.ContentLength = markdown.Length;
    await context.Response.BodyWriter.WriteAsync(markdown, context.RequestAborted);
}

internal static class MarkdownPlan
{
    internal static readonly QueryPlan<MarkdownBuffer> Instance = Create();

    private static QueryPlan<MarkdownBuffer> Create()
    {
        var html = StreamQuery.For<MarkdownBuffer>("html")
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
        html.Descendant("pre")
            .OnTextContent(static (ref output, in element) => output.FencedCode(element.TextUtf8));
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

        var table = html.Descendant("table")
            .OnStart(static (ref output, in _) => output.StartTable())
            .OnEnd(static (ref output) => output.EndTable());
        var row = table.Descendant("tr")
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
        return node
            .OnStart((ref MarkdownBuffer output, in Element _) => output.StartInlineBlock(ownedPrefix))
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
    private readonly SlidingByteBuffer _output = new(4 * 1024);
    private readonly ArrayBufferWriter<byte> _row = new(256);
    private readonly ArrayBufferWriter<byte> _linkTarget = new(128);
    private readonly ArrayBufferWriter<byte> _documentTitle = new(128);
    private readonly ArrayBufferWriter<byte> _inlinePrefix = new(16);
    private int _tableDepth;
    private int _rowCells;
    private int _inlineBlockDepth;
    private int _preferredArticleDepth;
    private bool _firstTableRow;
    private bool _inlineLink;
    private bool _inlineBlockHasContent;
    private bool _pendingInlineSpace;
    private bool _preferredArticleFound;
    private bool _preferredArticleComplete;

    internal ReadOnlyMemory<byte> WrittenMemory => _output.WrittenMemory;

    public ReadOnlyMemory<byte> CommittedUtf8 => _output.CommittedMemory;

    public void AdvanceCommitted(int bytes) => _output.AdvanceCommitted(bytes);

    private bool AcceptsContent =>
        !_preferredArticleFound || !_preferredArticleComplete && _preferredArticleDepth != 0;

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
        EnsureInlineBlockStarted();
        FlushInlineSpace();
        _linkTarget.Clear();
        Write(_linkTarget, href);
        Write("["u8);
        _inlineLink = true;
    }

    internal void EndInlineLink()
    {
        if (!_inlineLink)
            return;
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

    internal void StartTable()
    {
        if (!AcceptsContent)
            return;
        _tableDepth++;
        if (_tableDepth == 1)
            _firstTableRow = true;
    }

    internal void EndTable()
    {
        if (_tableDepth == 0)
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

internal sealed class SlidingByteBuffer(int initialCapacity) : IBufferWriter<byte>
{
    private byte[] _buffer = new byte[initialCapacity];
    private int _start;
    private int _committedEnd;
    private int _end;

    internal ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(_start, _end - _start);

    internal ReadOnlyMemory<byte> CommittedMemory => _buffer.AsMemory(_start, _committedEnd - _start);

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_end > _buffer.Length - count)
            throw new InvalidOperationException("Cannot advance past the available buffer.");
        _end += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_end);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_end);
    }

    internal void Commit() => _committedEnd = _end;

    internal void AdvanceCommitted(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes > _committedEnd - _start)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        _start += bytes;
        if (_start == _end)
            _start = _committedEnd = _end = 0;
    }

    internal void Clear() => _start = _committedEnd = _end = 0;

    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint < 0)
            throw new ArgumentOutOfRangeException(nameof(sizeHint));
        sizeHint = Math.Max(sizeHint, 1);
        if (sizeHint <= _buffer.Length - _end)
            return;

        var liveLength = _end - _start;
        if (sizeHint <= _buffer.Length - liveLength)
        {
            _buffer.AsSpan(_start, liveLength).CopyTo(_buffer);
            _committedEnd -= _start;
            _end = liveLength;
            _start = 0;
            return;
        }

        var newLength = Math.Max(_buffer.Length * 2, checked(liveLength + sizeHint));
        var replacement = new byte[newLength];
        _buffer.AsSpan(_start, liveLength).CopyTo(replacement);
        _committedEnd -= _start;
        _end = liveLength;
        _start = 0;
        _buffer = replacement;
    }
}
