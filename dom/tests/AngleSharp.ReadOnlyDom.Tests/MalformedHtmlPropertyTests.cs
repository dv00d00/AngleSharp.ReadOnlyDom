using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom;
using AngleSharp.ReadOnlyDom.Html;
using FsCheck;
using FsCheck.Fluent;
#if NET10_0
using AngleSharp.ReadOnlyDom.Compact.Document;
using AngleSharp.ReadOnlyDom.Compact.Parsing;
using AngleSharp.ReadOnlyDom.Compact.Projection;
#endif

namespace AngleSharp.Readonly.Tests;

public class MalformedHtmlPropertyTests
{
    private static readonly string[] Fragments =
    [
        "<div>",
        "</div>",
        "<p>",
        "</p>",
        "<p><div>",
        "<b><i>",
        "</b></i>",
        "<ul><li>",
        "</ul>",
        "<select><option>",
        "<select><div>",
        "<svg><foreignObject><p>",
        "<math><mi>",
        "<form><form><input>",
        "<a><a href=x>",
        "<x-custom data-x=1>",
        "</unknown>",
        "<div id='a' id='b'>",
        "<div class='unterminated>",
        "<div a==b c='d' e=\"f\">",
        "<input disabled= disabled>",
        "<!--",
        "<!-- --!>",
        "<!DOCTYPE",
        "<!DOCTYPE html PUBLIC>",
        "<![CDATA[broken]]>",
        "<?xml-stylesheet href='x'?>",
        "<script><div></script>",
        "<style></p></style>",
        "<textarea>&notit;</textarea>",
        "&amp;&notit;&#0;&#x110000;",
        "text\0after-null",
        "\r\n\t ",
        "plain text & broken; <",
        "é漢🙂",
    ];

    [Test]
    public void GeneratedMalformedHtmlMatchesAngleSharpMutableDom()
    {
        var property = Prop.ForAll(
            MalformedHtml().ToArbitrary(),
            source =>
            {
                using var mutable = new HtmlParser().ParseDocument(source);
                using var readOnly = ReadOnlyParser
                    .CreateParser(ReadOnlyMetadataProfile.Minimal)
                    .ParseReadOnlyDocument(source);

                var expected = Normalize(mutable);
                var actual = Normalize(readOnly);
                if (!expected.Equals(actual, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"DOM mismatch for generated HTML:\n{Escape(source)}\n\nMutable:\n{expected}\n\nRead-only:\n{actual}"
                    );
                }
            }
        );

        Check.One(Config.QuickThrowOnFailure.WithMaxTest(500), property);
    }

#if NET10_0
    [Test]
    public void GeneratedMalformedHtmlMatchesAngleSharpMutableDomForCompactArena()
    {
        var property = Prop.ForAll(
            MalformedHtml().ToArbitrary(),
            source =>
            {
                using var mutable = new HtmlParser().ParseDocument(source);
                using var compact = CompactParser.CreateParser().ParseCompactDocument(source);

                var expected = NormalizeForCompact(mutable);
                var actual = Normalize(compact);
                if (!expected.Equals(actual, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Compact DOM mismatch for generated HTML:\n{Escape(source)}\n\nMutable:\n{expected}\n\nCompact:\n{actual}"
                    );
                }
            }
        );

        Check.One(Config.QuickThrowOnFailure.WithMaxTest(10_000), property);
    }

    [Test]
    public void GeneratedMalformedHtmlMatchesAngleSharpForEofProjectionIdText()
    {
        var plan = CompactProjection
            .First(CompactProjectionSelector.Tag("div").WithId("content"))
            .Field("text", CompactFieldProjection.SelfNormalizedText())
            .Compile();
        var property = Prop.ForAll(
            MalformedHtml().ToArbitrary(),
            fragment =>
            {
                var source = $"<main><div id=content>{fragment}</div><aside>tail</aside></main>";
                using var mutable = new HtmlParser().ParseDocument(source);
                var expected = mutable.QuerySelector("div#content");
                var actual = plan.Execute(source);
                var expectedText = NormalizeWhitespace(expected?.TextContent ?? string.Empty);
                var actualText = actual.Rows.Count == 0 ? string.Empty : actual.Rows[0]["text"].ToString();
                if (actual.Rows.Count != (expected is not null ? 1 : 0) || actualText != expectedText)
                {
                    throw new InvalidOperationException(
                        $"EOF projection mismatch for generated HTML:\n{Escape(source)}\n\nMutable:\n{Escape(expectedText)}\n\nProjection:\n{Escape(actualText)}"
                    );
                }
            }
        );

        Check.One(Config.QuickThrowOnFailure.WithMaxTest(10_000), property);
    }

    [Test]
    public void GeneratedMalformedHtmlMatchesAngleSharpForEofProjectionText()
    {
        var plan = CompactProjection
            .First(CompactProjectionSelector.Tag("article").WithClass("result"))
            .Field("text", CompactFieldProjection.SelfNormalizedText())
            .Compile();
        var property = Prop.ForAll(
            MalformedHtml().ToArbitrary(),
            fragment =>
            {
                var source = $"<main><article class=result>{fragment}</article><aside>tail</aside></main>";
                using var mutable = new HtmlParser().ParseDocument(source);
                var expected = mutable.QuerySelector("article.result");
                var actual = plan.Execute(source);
                var expectedText = NormalizeWhitespace(expected?.TextContent ?? string.Empty);
                var actualText = actual.Rows.Count == 0 ? string.Empty : actual.Rows[0]["text"].ToString();
                if (actual.Rows.Count != (expected is null ? 0 : 1) || actualText != expectedText)
                {
                    throw new InvalidOperationException(
                        $"EOF projection mismatch for generated HTML:\n{Escape(source)}\n\nMutable:\n{Escape(expectedText)}\n\nProjection:\n{Escape(actualText)}"
                    );
                }
            }
        );

        Check.One(Config.QuickThrowOnFailure.WithMaxTest(10_000), property);
    }

#endif

    private static Gen<string> MalformedHtml()
    {
        var fragment = Gen.Frequency(
            (9, Gen.Elements(Fragments)),
            (
                1,
                from name in Gen.Elements("div", "span", "p", "li", "svg", "math", "x-z")
                from delimiter in Gen.Elements(">", "/> ", "", " attr='", " attr=\"")
                select $"<{name}{delimiter}"
            )
        );
        return from count in Gen.Choose(1, 48) from fragments in fragment.ListOf(count) select string.Concat(fragments);
    }

#if NET10_0
    private static string NormalizeWhitespace(string value)
    {
        var result = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = result.Length != 0;
                continue;
            }
            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }
            result.Append(character);
        }
        return result.ToString();
    }

    private static string NormalizeForCompact(IDocument document)
    {
        var result = new StringBuilder();
        AppendCompactChildren(document.ChildNodes, result);
        return result.ToString();
    }

    private static string Normalize(CompactDocument document)
    {
        var result = new StringBuilder();
        var root = document.GetNode(0);
        AppendCompactChildren(document, root.FirstChild, root.SubtreeEndExclusive, result);
        return result.ToString();
    }

    private static void AppendCompactChildren(INodeList children, StringBuilder result)
    {
        var text = new StringBuilder();
        foreach (var child in children)
        {
            if (child is IText textNode)
            {
                text.Append(textNode.Data);
            }
            else if (child is IElement element)
            {
                AppendText(text, result);
                result.Append("E[").Append(element.LocalName);
                foreach (
                    var attribute in element.Attributes.OrderBy(attribute => attribute.Name, StringComparer.Ordinal)
                )
                    result.Append('|').Append(attribute.Name).Append('=').Append(Escape(attribute.Value));
                result.Append("]{");
                AppendCompactChildren(
                    element is IHtmlTemplateElement template ? template.Content.ChildNodes : element.ChildNodes,
                    result
                );
                result.Append('}');
            }
        }
        AppendText(text, result);
    }

    private static void AppendCompactChildren(
        CompactDocument document,
        int child,
        int endExclusive,
        StringBuilder result
    )
    {
        var text = new StringBuilder();
        while (child >= 0 && child < endExclusive)
        {
            var node = document.GetNode(child);
            if (node.Kind == CompactNodeKind.Text && node.PayloadIndex >= 0)
            {
                var payload = document.GetPayload(node.PayloadIndex);
                text.Append(document.GetValue(payload.ValueStart, payload.ValueLength));
            }
            else if (node.Kind == CompactNodeKind.Element)
            {
                AppendText(text, result);
                result.Append("E[").Append(document.GetName(node.NameId));
                if (node.PayloadIndex >= 0)
                {
                    var payload = document.GetPayload(node.PayloadIndex);
                    var attributes = new List<(string Name, string Value)>(payload.AttributeCount);
                    for (var i = 0; i < payload.AttributeCount; i++)
                    {
                        var attribute = document.GetAttribute(payload.FirstAttribute + i);
                        attributes.Add(
                            (
                                document.GetName(attribute.NameId).ToString(),
                                Escape(document.GetValue(attribute.ValueStart, attribute.ValueLength).ToString())
                            )
                        );
                    }
                    attributes.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
                    foreach (var attribute in attributes)
                        result.Append('|').Append(attribute.Name).Append('=').Append(attribute.Value);
                }
                result.Append("]{");
                AppendCompactChildren(document, node.FirstChild, node.SubtreeEndExclusive, result);
                result.Append('}');
            }
            child = node.SubtreeEndExclusive;
        }
        AppendText(text, result);
    }
#endif

    private static string Normalize(IDocument document)
    {
        var result = new StringBuilder();
        AppendChildren(document.ChildNodes, result);
        return result.ToString();
    }

    private static string Normalize(IReadOnlyDocument document)
    {
        var result = new StringBuilder();
        AppendChildren(document.ChildNodes, result);
        return result.ToString();
    }

    private static void AppendChildren(INodeList children, StringBuilder result)
    {
        var text = new StringBuilder();
        foreach (var child in children)
        {
            if (child is IText textNode)
            {
                text.Append(textNode.Data);
            }
            else if (child is IElement element)
            {
                AppendText(text, result);
                AppendElement(element, result);
            }
        }

        AppendText(text, result);
    }

    private static void AppendChildren(IReadOnlyNodeList children, StringBuilder result)
    {
        var text = new StringBuilder();
        foreach (var child in children)
        {
            if (child is IReadOnlyTextNode textNode)
            {
                text.Append(textNode.Content.ToString());
            }
            else if (child is IReadOnlyElement element)
            {
                AppendText(text, result);
                AppendElement(element, result);
            }
        }

        AppendText(text, result);
    }

    private static void AppendElement(IElement element, StringBuilder result)
    {
        result.Append("E[").Append(element.NamespaceUri ?? string.Empty).Append('|').Append(element.LocalName);
        foreach (var attribute in element.Attributes.OrderBy(attribute => attribute.Name, StringComparer.Ordinal))
        {
            result.Append('|').Append(attribute.Name).Append('=').Append(Escape(attribute.Value));
        }

        result.Append("]{");
        AppendChildren(element.ChildNodes, result);
        result.Append('}');
    }

    private static void AppendElement(IReadOnlyElement element, StringBuilder result)
    {
        result.Append("E[").Append(element.NamespaceUri.ToString()).Append('|').Append(element.LocalName.ToString());
        foreach (
            var attribute in element.Attributes.OrderBy(attribute => attribute.Name.ToString(), StringComparer.Ordinal)
        )
        {
            result.Append('|').Append(attribute.Name.ToString()).Append('=').Append(Escape(attribute.Value.ToString()));
        }

        result.Append("]{");
        AppendChildren(element.ChildNodes, result);
        result.Append('}');
    }

    private static void AppendText(StringBuilder text, StringBuilder result)
    {
        var normalized = NormalizeMinimalText(text);
        if (normalized.Length != 0)
        {
            result.Append("T[").Append(Escape(normalized)).Append(']');
        }

        text.Clear();
    }

    private static string NormalizeMinimalText(StringBuilder text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var character in text.ToString())
        {
            if (!char.IsWhiteSpace(character))
                result.Append(character);
        }

        return result.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\0", "\\0").Replace("\r", "\\r").Replace("\n", "\\n");
}
