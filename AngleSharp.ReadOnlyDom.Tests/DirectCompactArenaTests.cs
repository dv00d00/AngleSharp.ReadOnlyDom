#if NET10_0
using System.Runtime.CompilerServices;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom;
using AngleSharp.ReadOnlyDom.CompactPrototype;
using AngleSharp.ReadOnlyDom.Html;

namespace AngleSharp.Readonly.Tests;

public sealed class DirectCompactArenaTests
{
    [Test]
    public async Task HotNodeIsExactlySixteenBytes() =>
        await Assert.That(Unsafe.SizeOf<HotCompactNode>()).IsEqualTo(16);

    [Test]
    public async Task InlineReferenceListLayoutsAreExplicit()
    {
        await Assert.That(Unsafe.SizeOf<SmallReferenceList<object>>()).IsEqualTo(32);
        await Assert.That(Unsafe.SizeOf<SmallReferenceList4<object>>()).IsEqualTo(48);
    }

    [Test]
    public async Task DirectArenaMatchesReadOnlyFactoryOnWildMarkup()
    {
        string[] corpus =
        [
            "<p>one<div>two</p>three",
            "<!doctype html><!--a--><html><head><title>x</title><body><ul><li>a<li>b</ul>",
            "<b><i>one</b>two</i>",
            "<template><section data-x='1'>inside</section></template><main>outside</main>",
            "text &amp; &#x1f600; <!-- broken",
        ];

        foreach (var html in corpus)
        {
            using var expected = new HtmlParser(
                new HtmlParserOptions { SkipComments = true, SkipProcessingInstructions = true }
            ).ParseDocument(html);
            using var actual = DirectCompactParser.Parse(html);
            await Assert.That(Snapshot(actual, 0)).IsEqualTo(Snapshot(expected));
        }
    }

    [Test]
    public async Task FactoryPathDocumentsForeignElementBoundary()
    {
        const string html = "<svg><foreignObject><div>x</div></foreignObject></svg>";
        using var expected = new HtmlParser().ParseDocument(html);
        using var actual = DirectCompactParser.Parse(html);

        await Assert.That(Snapshot(actual, 0)).IsNotEqualTo(Snapshot(expected));
        await Assert.That(Snapshot(expected)).Contains("foreignObject[]{Element:div");
    }

    [Test]
    public async Task MetadataColumnsRemainOptional()
    {
        using var minimal = DirectCompactParser.Parse("<main><p>x</p></main>");
        using var navigable = DirectCompactParser.Parse(
            "<main><p>x</p></main>",
            CompactMetadataOptions.ParentLinks | CompactMetadataOptions.SourceLocations
        );
        await Assert.That(minimal.HasParentLinks).IsFalse();
        await Assert.That(minimal.HasSourceLocations).IsFalse();
        await Assert.That(navigable.HasParentLinks).IsTrue();
        await Assert.That(navigable.HasSourceLocations).IsTrue();
        await Assert.That(navigable.GetParent(1)).IsEqualTo(0);
    }

    [Test]
    public async Task PooledDocumentOwnsAndReturnsItsBuffers()
    {
        var document = DirectCompactParser.Parse("<main><p>x</p></main>", ownership: CompactBufferOwnership.Pooled);
        await Assert.That(document.NodeCount).IsGreaterThan(3);
        await Assert.That(document.FindNameId("main")).IsNotEqualTo(ushort.MaxValue);
        document.Dispose();
        document.Dispose();
    }

    [Test]
    public async Task ReusableSessionCreatesIndependentDocuments()
    {
        var session = new DirectCompactParserSession(ownership: CompactBufferOwnership.Pooled);
        using var first = session.Parse("<main><p>first</p></main>");
        var firstCount = first.NodeCount;
        first.Dispose();

        using var second = session.Parse("<main><p>second</p><p>third</p></main>");
        await Assert.That(second.NodeCount).IsGreaterThan(firstCount);
        await Assert.That(second.FindNameId("main")).IsNotEqualTo(ushort.MaxValue);
    }

    private static string Snapshot(INode node)
    {
        var name = node is IElement namedElement ? namedElement.LocalName : node.NodeName;
        var result = $"{Kind(node)}:{name}[";
        if (node is IElement element)
            result += string.Join(",", element.Attributes.Select(attribute => $"{attribute.Name}={attribute.Value}"));
        result += "]{";
        var children = node is IHtmlTemplateElement template ? template.Content.ChildNodes : node.ChildNodes;
        foreach (var child in children)
        {
            if (child.NodeType is not (NodeType.Comment or NodeType.ProcessingInstruction))
                result += Snapshot(child);
        }
        return result + "}";
    }

    private static string Snapshot(IReadOnlyNode node)
    {
        var result = $"{Kind(node)}:{node.NodeName}[";
        if (node is IReadOnlyElement element)
            result += string.Join(",", element.Attributes.Select(attribute => $"{attribute.Name}={attribute.Value}"));
        result += "]{";
        var children = node is IReadOnlyTemplateElement template ? template.Content : node.ChildNodes;
        foreach (var child in children)
            result += Snapshot(child);
        return result + "}";
    }

    private static string Snapshot(HotCompactDocument document, int handle)
    {
        ref readonly var node = ref document.GetNode(handle);
        var result = $"{node.Kind}:{document.GetName(node.NameId)}[";
        if (node.PayloadIndex >= 0)
        {
            ref readonly var payload = ref document.GetPayload(node.PayloadIndex);
            for (var i = 0; i < payload.AttributeCount; i++)
            {
                if (i != 0)
                    result += ",";
                ref readonly var attribute = ref document.GetAttribute(payload.FirstAttribute + i);
                result +=
                    $"{document.GetName(attribute.NameId)}={document.GetValue(attribute.ValueStart, attribute.ValueLength)}";
            }
        }
        result += "]{";
        var child = node.FirstChild;
        while (child >= 0)
        {
            result += Snapshot(document, child);
            child = document.GetNode(child).NextSibling;
        }
        return result + "}";
    }

    private static CompactNodeKind Kind(INode node) =>
        node.NodeType switch
        {
            NodeType.Document => CompactNodeKind.Document,
            NodeType.Element => CompactNodeKind.Element,
            NodeType.ProcessingInstruction => CompactNodeKind.ProcessingInstruction,
            NodeType.Comment => CompactNodeKind.Comment,
            NodeType.Text => CompactNodeKind.Text,
            _ => CompactNodeKind.Other,
        };

    private static CompactNodeKind Kind(IReadOnlyNode node) =>
        node switch
        {
            IReadOnlyDocument => CompactNodeKind.Document,
            IReadOnlyElement => CompactNodeKind.Element,
            IReadOnlyProcessingInstructionNode => CompactNodeKind.ProcessingInstruction,
            IReadOnlyCommentNode => CompactNodeKind.Comment,
            IReadOnlyTextNode => CompactNodeKind.Text,
            _ => CompactNodeKind.Other,
        };
}
#endif
