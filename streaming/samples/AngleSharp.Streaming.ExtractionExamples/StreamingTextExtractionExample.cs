using AngleSharp.ReadOnlyDom.Streaming.Query;

namespace AngleSharp.Streaming.ExtractionExamples;

internal static class StreamingTextExtractionExample
{
    private static readonly string[] IgnoredElements = ["script", "style", "template", "noscript"];

    private static readonly string[] BlockElements =
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

    private static readonly QueryPlan<TextState> Plan = CreatePlan();

    internal static string Extract(ReadOnlySpan<byte> htmlUtf8) => Plan.Execute(htmlUtf8, new TextState()).Value;

    private static QueryPlan<TextState> CreatePlan()
    {
        var content = StreamQuery
            .For<TextState>("body")
            .OnText(static (ref TextState state, ReadOnlySpan<byte> text) => state.Append(text));

        foreach (var name in IgnoredElements)
        {
            content
                .Descendant(name)
                .OnStart(static (ref TextState state, in Element _) => state.StartIgnored())
                .OnEnd(static (ref TextState state) => state.EndIgnored());
        }

        foreach (var name in BlockElements)
        {
            content
                .Descendant(name)
                .OnStart(static (ref TextState state, in Element _) => state.ParagraphBreak())
                .OnEnd(static (ref TextState state) => state.ParagraphBreak());
        }

        content.Descendant("br").OnEnd(static (ref TextState state) => state.LineBreak());
        content.Descendant("td").OnEnd(static (ref TextState state) => state.CellBreak());
        content.Descendant("th").OnEnd(static (ref TextState state) => state.CellBreak());
        content
            .Descendant("img")
            .OnClose(
                static (ref TextState state, in CompletedElement image) =>
                {
                    if (image.TryGetAttributeUtf8("alt"u8, out var alt))
                        state.ImageAlt(alt);
                },
                "alt"
            );

        return content.Compile();
    }

    private sealed class TextState
    {
        private readonly NormalizedTextOutput _output = new();
        private int _ignoredDepth;

        internal string Value => _output.Value;

        internal void Append(ReadOnlySpan<byte> text)
        {
            if (_ignoredDepth == 0)
                _output.AppendUtf8(text);
        }

        internal void StartIgnored() => _ignoredDepth++;

        internal void EndIgnored()
        {
            if (_ignoredDepth > 0)
                _ignoredDepth--;
        }

        internal void ImageAlt(ReadOnlySpan<byte> alt)
        {
            if (_ignoredDepth != 0 || alt.IsEmpty)
                return;
            _output.Space();
            _output.AppendUtf8(alt);
            _output.Space();
        }

        internal void CellBreak()
        {
            if (_ignoredDepth == 0)
                _output.CellBreak();
        }

        internal void LineBreak()
        {
            if (_ignoredDepth == 0)
                _output.LineBreak();
        }

        internal void ParagraphBreak()
        {
            if (_ignoredDepth == 0)
                _output.ParagraphBreak();
        }
    }
}
