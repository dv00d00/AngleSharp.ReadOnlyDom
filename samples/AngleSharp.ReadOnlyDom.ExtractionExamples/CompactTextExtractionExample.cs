using AngleSharp.ReadOnlyDom.Compact.Document;
using AngleSharp.ReadOnlyDom.Compact.Parsing;
using AngleSharp.ReadOnlyDom.Compact.Query;

namespace AngleSharp.ReadOnlyDom.ExtractionExamples;

internal static class CompactTextExtractionExample
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

    internal static string Extract(string html)
    {
        var parser = CompactParser.CreateParser();
        using var document = parser.ParseCompactDocument(html);
        var body = document.Elements("body").First();
        if (!body.Exists)
            return String.Empty;

        var output = new NormalizedTextOutput();
        Visit(body, output);
        return output.Value;
    }

    private static void Visit(Node node, NormalizedTextOutput output)
    {
        if (node.Kind == CompactNodeKind.Text)
        {
            output.Append(node.Text());
            return;
        }
        if (!node.IsElement)
            return;

        var tag = node.LocalName;
        if (Contains(IgnoredElements, tag))
            return;
        if (tag.Equals("br", StringComparison.OrdinalIgnoreCase))
        {
            output.LineBreak();
            return;
        }
        if (tag.Equals("img", StringComparison.OrdinalIgnoreCase) && node.HasAttr("alt"))
        {
            output.Space();
            output.Append(node.Attr("alt"));
            output.Space();
        }

        var block = Contains(BlockElements, tag);
        if (block)
            output.ParagraphBreak();
        foreach (var child in node.Children())
            Visit(child, output);
        if (
            tag.Equals("td", StringComparison.OrdinalIgnoreCase) || tag.Equals("th", StringComparison.OrdinalIgnoreCase)
        )
            output.CellBreak();
        else if (block)
            output.ParagraphBreak();
    }

    private static bool Contains(IEnumerable<string> values, ReadOnlySpan<char> tag)
    {
        foreach (var value in values)
        {
            if (tag.Equals(value, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}