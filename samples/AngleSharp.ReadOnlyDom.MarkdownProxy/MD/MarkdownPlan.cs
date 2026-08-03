using AngleSharp.ReadOnlyDom.Streaming.Query;

namespace AngleSharp.ReadOnlyDom.MarkdownProxy.MD
{
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
            html.Descendant("a").Attribute("href").AsInlineLink();
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
                .Id("hnmain")
                .OnStart(static (ref output, in _) => output.StartLayoutTable())
                .OnEnd(static (ref output) => output.EndLayoutTable());
            html.Descendant("tr").Class("athing").AsInlineBlock("- "u8);
            html.Descendant("span").Class("subline").AsInlineBlock("  "u8);

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
}
