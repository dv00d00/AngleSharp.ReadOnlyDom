using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom;
using AngleSharp.ReadOnlyDom.Html;
using AngleSharp.ReadOnlyDom.Html.Model;

namespace AngleSharp.Readonly.Tests;

public class ContractTests
{
    [Test]
    public async Task DocumentNavigationUsesTheHtmlTree()
    {
        var parser = new HtmlParser(default, ReadOnlyParser.DefaultContext);
        using var document = parser.ParseReadOnlyDocument(
            "<!doctype html><!--before--><title>x</title><main>content</main>"
        );

        await Assert.That(document.DocumentElement.LocalName.ToString()).IsEqualTo("html");
        await Assert.That(document.Head.LocalName.ToString()).IsEqualTo("head");
        await Assert.That(document.Body.LocalName.ToString()).IsEqualTo("body");
        await Assert.That(document.DocumentElement.Parent).IsSameReferenceAs(document);
        await Assert.That(document.Head.Parent).IsSameReferenceAs(document.DocumentElement);
        await Assert.That(document.Body.Parent).IsSameReferenceAs(document.DocumentElement);
    }

    [Test]
    public async Task CharacterDataExposesDomNodeNamesWithoutStoringThemPerNode()
    {
        var text = new ReadOnlyTextNode(null, "content");
        var comment = new ReadOnlyComment(null, "note");

        await Assert.That(text.NodeName.ToString()).IsEqualTo("#text");
        await Assert.That(text.Content.ToString()).IsEqualTo("content");
        await Assert.That(comment.NodeName.ToString()).IsEqualTo("#comment");
        await Assert.That(comment.Content.ToString()).IsEqualTo("note");
    }

    [Test]
    public async Task ProcessingInstructionSeparatesTargetAndContent()
    {
        var instruction = ReadOnlyProcessingInstruction.Create(null, "xml-stylesheet href='theme.css'");
        using var writer = new StringWriter();
        instruction.Print(writer);

        await Assert.That(instruction.NodeName.ToString()).IsEqualTo("xml-stylesheet");
        await Assert.That(instruction.Target.ToString()).IsEqualTo("xml-stylesheet");
        await Assert.That(instruction.Content.ToString()).IsEqualTo(" href='theme.css'");
        await Assert.That(writer.ToString()).IsEqualTo("<?xml-stylesheet href='theme.css'?>");
    }

    [Test]
    public async Task ShallowCopiesPreserveQualifiedNames()
    {
        var svg = new ReadOnlySvgElement(null, "circle", "s");
        var math = new ReadOnlyMathElement(null, "mi", "m");

        var svgCopy = (IReadOnlyElement)svg.ShallowCopy();
        var mathCopy = (IReadOnlyElement)math.ShallowCopy();

        await Assert.That(svgCopy.NodeName.ToString()).IsEqualTo("s:circle");
        await Assert.That(svgCopy.Prefix.ToString()).IsEqualTo("s");
        await Assert.That(mathCopy.NodeName.ToString()).IsEqualTo("m:mi");
        await Assert.That(mathCopy.Prefix.ToString()).IsEqualTo("m");
    }

    [Test]
    [Arguments("<main><p>valid <b>tree</b></p></main>")]
    [Arguments("<p><b>one<i>two</b>three</i>four")]
    [Arguments("<table>before<tr><td>one<td>two</table>after")]
    [Arguments("<svg><foreignObject><p>x</p></foreignObject></svg><math><mi>y</mi></math>")]
    public async Task NavigationMatchesStandardAngleSharp(string source)
    {
        var standardParser = new HtmlParser();
        var readOnlyParser = new HtmlParser(default, ReadOnlyParser.DefaultContext);
        using var standard = standardParser.ParseDocument(source);
        using var readOnly = readOnlyParser.ParseReadOnlyDocument(source);

        var expected = standard.All.Select(element => $"{element.NamespaceUri}|{element.Prefix}|{element.LocalName}");
        var actual = readOnly
            .AllDescendants()
            .OfType<IReadOnlyElement>()
            .Select(element => $"{element.NamespaceUri}|{element.Prefix}|{element.LocalName}");

        await Assert.That(string.Join("\n", actual)).IsEqualTo(string.Join("\n", expected));
        await Assert.That(readOnly.Body.GetTextContent()).IsEqualTo(standard.Body!.TextContent);
        await Assert.That(Serialize(readOnly)).IsEqualTo(Serialize(standard));
    }

    private static string Serialize(INode root)
    {
        var result = new StringBuilder();
        Append(root, result);
        return result.ToString();

        static void Append(INode node, StringBuilder result)
        {
            if (node is IElement element)
            {
                result.Append('<').Append(element.NamespaceUri).Append('|').Append(element.LocalName.ToString());
                foreach (var attribute in element.Attributes)
                {
                    result.Append(' ').Append(attribute.Name.ToString()).Append('=').Append(attribute.Value.ToString());
                }

                result.Append('>');
            }
            else if (node is IText text)
            {
                result.Append("#text[").Append(text.Data).Append(']');
            }
            else if (node is IComment comment)
            {
                result.Append("#comment[").Append(comment.Data).Append(']');
            }

            foreach (var child in node.ChildNodes)
            {
                Append(child, result);
            }

            if (node is IElement closingElement)
            {
                result.Append("</").Append(closingElement.LocalName).Append('>');
            }
        }
    }

    private static string Serialize(IReadOnlyNode root)
    {
        var result = new StringBuilder();
        Append(root, result);
        return result.ToString();

        static void Append(IReadOnlyNode node, StringBuilder result)
        {
            var isDocument = node is IReadOnlyDocument;
            if (!isDocument && node is IReadOnlyElement element)
            {
                result
                    .Append('<')
                    .Append(element.NamespaceUri.ToString())
                    .Append('|')
                    .Append(element.LocalName.ToString());
                foreach (var attribute in element.Attributes)
                {
                    result.Append(' ').Append(attribute.Name.ToString()).Append('=').Append(attribute.Value.ToString());
                }

                result.Append('>');
            }
            else if (node is IReadOnlyTextNode text)
            {
                result.Append("#text[").Append(text.Content.ToString()).Append(']');
            }
            else if (node is IReadOnlyCommentNode comment)
            {
                result.Append("#comment[").Append(comment.Content.ToString()).Append(']');
            }

            foreach (var child in node.ChildNodes)
            {
                Append(child, result);
            }

            if (!isDocument && node is IReadOnlyElement closingElement)
            {
                result.Append("</").Append(closingElement.LocalName.ToString()).Append('>');
            }
        }
    }
}
