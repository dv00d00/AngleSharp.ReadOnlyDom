using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom;
using AngleSharp.ReadOnlyDom.Html;

namespace AngleSharp.Readonly.Tests;

public class ConstructionTests
{
    [Test]
    public async Task MeaningfulWhitespaceAfterInlineContentIsPreserved()
    {
        const string html = "<article id=content><p>one <span>two</span> </p><p>three</p></article>";
        var mutableParser = new HtmlParser();
        var readOnlyParser = new HtmlParser(default, ReadOnlyParser.DefaultContext);

        using var mutable = mutableParser.ParseDocument(html);
        using var readOnly = readOnlyParser.ParseReadOnlyDocument(html);
        var expected = mutable.QuerySelector("#content")!.TextContent;
        var actual = readOnly.QueryOne(node => node.TagId("article", "content"))!.GetTextContent();

        await Assert.That(expected).IsEqualTo("one two three");
        await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    public async Task StructuralWhitespaceBetweenBlockSiblingsRemainsDiscarded()
    {
        const string html = "<body>\n  <div>one</div>\n  <div>two</div>\n</body>";
        var parser = new HtmlParser(default, ReadOnlyParser.DefaultContext);

        using var document = parser.ParseReadOnlyDocument(html);
        var body = document.QueryOne(node => node.Tag("body"))!;

        await Assert.That(body.ChildNodes.Length).IsEqualTo(2);
        await Assert.That(body.ChildNodes.All(node => node is IReadOnlyElement)).IsTrue();
    }

    [Test]
    public async Task StructuralFormFeedBetweenBlockSiblingsRemainsDiscarded()
    {
        const string html = "<body>\f<div>one</div>\f<div>two</div>\f</body>";
        var parser = new HtmlParser(default, ReadOnlyParser.DefaultContext);

        using var document = parser.ParseReadOnlyDocument(html);
        var body = document.QueryOne(node => node.Tag("body"))!;

        await Assert.That(body.ChildNodes.Length).IsEqualTo(2);
        await Assert.That(body.ChildNodes.All(node => node is IReadOnlyElement)).IsTrue();
    }

    [Test]
    public async Task CompactCollectionsPreserveConstructionResults()
    {
        string[] samples =
        [
            "<main><p>single</p></main>",
            "<main><p>one</p><p>two</p><p>three</p></main>",
            "<main id='first' class='a b' data-value='x'><span title='value'>text</span></main>",
            "<p><b>one<i>two</b>three</i>four",
            "<table>before<tr><td>one<td>two</table>after",
            "<template><section><strong>content</strong></section></template>",
            "<main>one<!-- comment --><span>two</span>three<br>four</main>",
        ];

        var mutableParser = new HtmlParser();
        var readOnlyParser = new HtmlParser(default, ReadOnlyParser.DefaultContext);

        foreach (var sample in samples)
        {
            using var mutable = mutableParser.ParseDocument(sample);
            using var readOnly = readOnlyParser.ParseReadOnlyDocument(sample);

            var expectedElements = string.Join(
                "|",
                mutable.All.Select(element =>
                    $"{element.LocalName}:{string.Join(",", element.Attributes.Select(attribute => $"{attribute.Name}={attribute.Value}"))}"
                )
            );
            var actualElements = string.Join(
                "|",
                readOnly
                    .AllDescendants()
                    .OfType<IReadOnlyElement>()
                    .Select(element =>
                        $"{element.LocalName}:{string.Join(",", element.Attributes.Select(attribute => $"{attribute.Name}={attribute.Value}"))}"
                    )
            );

            var mutableBody = mutable.Body!;
            var readOnlyBody = readOnly.QueryOne(node => node.Tag("body"))!;

            await Assert.That(actualElements).IsEqualTo(expectedElements);
            await Assert.That(readOnlyBody.GetTextContent()).IsEqualTo(mutableBody.TextContent);
        }
    }
}
