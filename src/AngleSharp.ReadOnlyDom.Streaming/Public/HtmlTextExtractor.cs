using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace AngleSharp.ReadOnlyDom.Streaming;

/// <summary>Configures the default query-directed HTML-to-text view.</summary>
public sealed class HtmlTextOptions
{
    public string ContentElement { get; init; } = "body";

    public IReadOnlyCollection<string> IgnoredElements { get; init; } = ["script", "style", "template", "noscript"];

    public IReadOnlyCollection<string> BlockElements { get; init; } =
    [
        "address",
        "article",
        "aside",
        "blockquote",
        "div",
        "dl",
        "fieldset",
        "figcaption",
        "figure",
        "footer",
        "form",
        "h1",
        "h2",
        "h3",
        "h4",
        "h5",
        "h6",
        "header",
        "li",
        "main",
        "nav",
        "ol",
        "p",
        "pre",
        "section",
        "table",
        "tr",
        "ul",
    ];

    public IReadOnlyCollection<string> LineBreakElements { get; init; } = ["br"];

    public IReadOnlyCollection<string> CellElements { get; init; } = ["td", "th"];

    public string LineSeparator { get; init; } = "\n";

    public string ParagraphSeparator { get; init; } = "\n\n";

    public string CellSeparator { get; init; } = "\t";

    public bool IncludeImageAltText { get; init; } = true;
}

/// <summary>
/// A compiled, reusable HTML-to-text view which consumes UTF-8 without materializing a DOM.
/// </summary>
public sealed class HtmlTextExtractor
{
    private readonly QueryPlan<ExtractionState> _plan;
    private readonly ExtractorSettings _settings;

    public HtmlTextExtractor(HtmlTextOptions? options = null)
    {
        _settings = new ExtractorSettings(options ?? new HtmlTextOptions());
        _plan = CreatePlan(_settings);
    }

    public static HtmlTextExtractor Default { get; } = new();

    public QueryExplanation Explanation => _plan.Explanation;

    public string Extract(ReadOnlySpan<byte> htmlUtf8)
    {
        var output = new ArrayBufferWriter<byte>();
        Extract(htmlUtf8, output);
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    public byte[] ExtractUtf8(ReadOnlySpan<byte> htmlUtf8)
    {
        var output = new ArrayBufferWriter<byte>();
        Extract(htmlUtf8, output);
        return output.WrittenSpan.ToArray();
    }

    public void Extract(ReadOnlySpan<byte> htmlUtf8, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _plan.Execute(htmlUtf8, new ExtractionState(output, _settings));
    }

    public async ValueTask ExtractAsync(
        PipeReader reader,
        IBufferWriter<byte> output,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(output);
        await _plan
            .ExecuteAsync(reader, new ExtractionState(output, _settings), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask ExtractAsync(
        PipeReader reader,
        PipeWriter writer,
        int flushThreshold = 16 * 1024,
        int inputSliceSize = 4 * 1024,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        await _plan
            .ExecuteAsync(
                reader,
                writer,
                new ExtractionState(writer, _settings),
                flushThreshold,
                inputSliceSize,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static QueryPlan<ExtractionState> CreatePlan(ExtractorSettings settings)
    {
        var html = StreamQuery.For<ExtractionState>("html");
        var content = html.Descendant(settings.ContentElement)
            .OnText(static (ref ExtractionState state, ReadOnlySpan<byte> text) => state.Append(text));

        foreach (var name in settings.IgnoredElements)
        {
            content
                .Descendant(name)
                .OnStart(static (ref ExtractionState state, in Element _) => state.StartIgnored())
                .OnEnd(static (ref ExtractionState state) => state.EndIgnored());
        }

        foreach (var name in settings.BlockElements)
        {
            content
                .Descendant(name)
                .OnStart(static (ref ExtractionState state, in Element _) => state.ParagraphBreak())
                .OnEnd(static (ref ExtractionState state) => state.ParagraphBreak());
        }
        foreach (var name in settings.LineBreakElements)
            content.Descendant(name).OnEnd(static (ref ExtractionState state) => state.LineBreak());
        foreach (var name in settings.CellElements)
            content.Descendant(name).OnEnd(static (ref ExtractionState state) => state.CellBreak());

        if (settings.IncludeImageAltText)
        {
            content
                .Descendant("img")
                .OnClose(
                    static (ref ExtractionState state, in CompletedElement element) =>
                    {
                        if (element.TryGetAttributeUtf8("alt"u8, out var alt))
                            state.ImageAlt(alt);
                    },
                    "alt"
                );
        }

        return html.Compile();
    }

    private sealed class ExtractionState
    {
        private readonly NormalizedUtf8Writer _writer;
        private int _ignoredDepth;

        internal ExtractionState(IBufferWriter<byte> output, ExtractorSettings settings)
        {
            _writer = new NormalizedUtf8Writer(
                output,
                settings.LineSeparator,
                settings.ParagraphSeparator,
                settings.CellSeparator
            );
        }

        internal void Append(ReadOnlySpan<byte> utf8)
        {
            if (_ignoredDepth != 0)
                return;
            _writer.Append(utf8);
        }

        internal void StartIgnored() => _ignoredDepth++;

        internal void ImageAlt(ReadOnlySpan<byte> alt)
        {
            if (_ignoredDepth != 0 || alt.IsEmpty)
                return;
            _writer.Space();
            _writer.Append(alt);
            _writer.Space();
        }

        internal void EndIgnored()
        {
            if (_ignoredDepth != 0)
                _ignoredDepth--;
        }

        internal void CellBreak()
        {
            if (_ignoredDepth == 0)
                _writer.CellBreak();
        }

        internal void LineBreak()
        {
            if (_ignoredDepth == 0)
                _writer.LineBreak();
        }

        internal void ParagraphBreak()
        {
            if (_ignoredDepth == 0)
                _writer.ParagraphBreak();
        }

    }

    private sealed class ExtractorSettings
    {
        internal ExtractorSettings(HtmlTextOptions options)
        {
            ContentElement = Required(options.ContentElement, nameof(options.ContentElement));
            IgnoredElements = Snapshot(options.IgnoredElements, nameof(options.IgnoredElements));
            BlockElements = Snapshot(options.BlockElements, nameof(options.BlockElements));
            LineBreakElements = Snapshot(options.LineBreakElements, nameof(options.LineBreakElements));
            CellElements = Snapshot(options.CellElements, nameof(options.CellElements));
            LineSeparator = Encoding.UTF8.GetBytes(
                options.LineSeparator ?? throw new ArgumentNullException(nameof(options.LineSeparator))
            );
            ParagraphSeparator = Encoding.UTF8.GetBytes(
                options.ParagraphSeparator ?? throw new ArgumentNullException(nameof(options.ParagraphSeparator))
            );
            CellSeparator = Encoding.UTF8.GetBytes(
                options.CellSeparator ?? throw new ArgumentNullException(nameof(options.CellSeparator))
            );
            IncludeImageAltText = options.IncludeImageAltText;
        }

        internal string ContentElement { get; }
        internal string[] IgnoredElements { get; }
        internal string[] BlockElements { get; }
        internal string[] LineBreakElements { get; }
        internal string[] CellElements { get; }
        internal ReadOnlyMemory<byte> LineSeparator { get; }
        internal ReadOnlyMemory<byte> ParagraphSeparator { get; }
        internal ReadOnlyMemory<byte> CellSeparator { get; }
        internal bool IncludeImageAltText { get; }

        private static string[] Snapshot(IReadOnlyCollection<string>? values, string parameterName)
        {
            ArgumentNullException.ThrowIfNull(values, parameterName);
            return values
                .Select(value => Required(value, parameterName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string Required(string? value, string parameterName)
        {
            if (String.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Element names cannot be empty.", parameterName);
            return value;
        }
    }
}
