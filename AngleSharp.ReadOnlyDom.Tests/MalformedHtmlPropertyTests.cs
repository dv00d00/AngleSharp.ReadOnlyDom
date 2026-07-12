using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom;
using AngleSharp.ReadOnlyDom.Html;
using FsCheck;
using FsCheck.Fluent;

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
        var fragment = Gen.Frequency(
            (9, Gen.Elements(Fragments)),
            (
                1,
                from name in Gen.Elements("div", "span", "p", "li", "svg", "math", "x-z")
                from delimiter in Gen.Elements(">", "/> ", "", " attr='", " attr=\"")
                select $"<{name}{delimiter}"
            )
        );
        var html =
            from count in Gen.Choose(1, 48)
            from fragments in fragment.ListOf(count)
            select string.Concat(fragments);
        var property = Prop.ForAll(
            html.ToArbitrary(),
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
