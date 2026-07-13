#if NET10_0
using System.Runtime.CompilerServices;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.ReadOnlyDom;
using AngleSharp.ReadOnlyDom.CompactPrototype;
using AngleSharp.ReadOnlyDom.Filters;
using AngleSharp.ReadOnlyDom.Html;

namespace AngleSharp.Readonly.Tests;

public sealed class CompactParserTests
{
    [Test]
    public async Task CoreNodeIsExactlySixteenBytes() => await Assert.That(Unsafe.SizeOf<CompactNode>()).IsEqualTo(16);

    [Test]
    public async Task AppendOnlyDocumentsFreezeColumnsByDefault()
    {
        using var document = CompactParser.Parse("<main><p>x</p></main>");

        await Assert.That(document.Layout).IsEqualTo(CompactDocumentLayout.FrozenColumns);
    }

    [Test]
    public async Task PackedLayoutRemainsExplicitlyAvailable()
    {
        using var document = CompactParser.Parse("<main><p>x</p></main>", layout: CompactDocumentLayout.Packed);

        await Assert.That(document.Layout).IsEqualTo(CompactDocumentLayout.Packed);
    }

    [Test]
    [Arguments(CompactDocumentLayout.FrozenColumns)]
    [Arguments(CompactDocumentLayout.Packed)]
    public async Task ElementQueriesExcludeNonElementNames(CompactDocumentLayout layout)
    {
        using var document = CompactParser.Parse("<main>text</main>", layout: layout);

        await Assert.That(document.Elements("#text").Count()).IsEqualTo(0);
    }

    [Test]
    public async Task MutationHeavyMarkupFallsBackToPackedLayout()
    {
        using var document = CompactParser.Parse("<main><table>before<tr><td>x</td></tr></table></main>");

        await Assert.That(document.Layout).IsEqualTo(CompactDocumentLayout.Packed);
    }

    [Test]
    public async Task InlineReferenceListLayoutsAreExplicit()
    {
        await Assert.That(Unsafe.SizeOf<SmallReferenceList2<object>>()).IsEqualTo(32);
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
            "<b class='same'><b class='same'><b class='same'><b class='same'>x</b></b></b></b>",
            "<template><section data-x='1'>inside</section></template><main>outside</main>",
            "text &amp; &#x1f600; <!-- broken",
        ];

        foreach (var html in corpus)
        {
            using var expected = new HtmlParser(
                new HtmlParserOptions { SkipComments = true, SkipProcessingInstructions = true }
            ).ParseDocument(html);
            using var actual = CompactParser.Parse(html);
            await Assert.That(Snapshot(actual, 0)).IsEqualTo(Snapshot(expected));
        }
    }

    [Test]
    public async Task FactoryPathDocumentsForeignElementBoundary()
    {
        const string html = "<svg><foreignObject><div>x</div></foreignObject></svg>";
        using var expected = new HtmlParser().ParseDocument(html);
        using var actual = CompactParser.Parse(html);

        await Assert.That(Snapshot(actual, 0)).IsNotEqualTo(Snapshot(expected));
        await Assert.That(Snapshot(expected)).Contains("foreignObject[]{Element:div");
    }

    [Test]
    public async Task MetadataColumnsRemainOptional()
    {
        using var minimal = CompactParser.Parse("<main><p>x</p></main>");
        using var navigable = CompactParser.Parse(
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
        var document = CompactParser.Parse("<main><p>x</p></main>");
        await Assert.That(document.NodeCount).IsGreaterThan(3);
        await Assert.That(document.FindNameId("main")).IsNotEqualTo(ushort.MaxValue);
        document.Dispose();
        document.Dispose();
    }

    [Test]
    public async Task ReusableSessionCreatesIndependentDocuments()
    {
        var session = new CompactParserSession();
        using var first = session.Parse("<main><p>first</p></main>");
        var firstCount = first.NodeCount;
        first.Dispose();

        using var second = session.Parse("<main><p>second</p><p>third</p></main>");
        await Assert.That(second.NodeCount).IsGreaterThan(firstCount);
        await Assert.That(second.FindNameId("main")).IsNotEqualTo(ushort.MaxValue);
    }

    [Test]
    public async Task KnownAndCustomNamesShareStableDocumentIds()
    {
        using var document = CompactParser.Parse("<main><x-widget data-custom='x'></x-widget></main>");

        var main = document.FindNameId("main");
        var customElement = document.FindNameId("x-widget");
        var customAttribute = document.FindNameId("data-custom");
        await Assert.That(main).IsNotEqualTo(ushort.MaxValue);
        await Assert.That(customElement).IsNotEqualTo(ushort.MaxValue);
        await Assert.That(customAttribute).IsNotEqualTo(ushort.MaxValue);
        await Assert.That(document.GetName(main)).IsEqualTo("main");
        await Assert.That(document.GetName(customElement)).IsEqualTo("x-widget");
        await Assert.That(document.GetName(customAttribute)).IsEqualTo("data-custom");
        await Assert.That(document.FindNameId("aside")).IsEqualTo(ushort.MaxValue);
    }

    [Test]
    public async Task ExtractionProfileDisablesNonExtractionFeatures()
    {
        var options = CompactParserProfiles.Extraction;

        await Assert.That(options.IsScripting).IsFalse();
        await Assert.That(options.IsNotSupportingFrames).IsTrue();
        await Assert.That(options.IsKeepingSourceReferences).IsFalse();
        await Assert.That(options.IsPreservingAttributeNames).IsFalse();
        await Assert.That(options.DisableElementPositionTracking).IsTrue();
        await Assert.That(options.SkipComments).IsTrue();
        await Assert.That(options.SkipProcessingInstructions).IsTrue();
        await Assert.That(options.SkipScriptText).IsTrue();
        await Assert.That(options.SkipRawText).IsTrue();
    }

    [Test]
    public async Task AttributeFilteringMatchesAngleSharpAcrossDiverseShapes()
    {
        string[] corpus =
        [
            "<main><p>attribute free</p></main>",
            "<article id='a'><a class='link' href='/x'>x</a></article>",
            "<form action='/save' method='post'><input id='x' class='field' name='x' value='1' required></form>",
            "<table data-table='x'>text<tr><td id='cell' colspan='2'>x</td></tr></table>",
            "<template data-template='x'><b id='b'><i class='i'>one</b>two</i></template>",
        ];

        foreach (var html in corpus)
        {
            var expectedParser = new HtmlParser(
                new HtmlParserOptions
                {
                    SkipComments = true,
                    SkipProcessingInstructions = true,
                    ShouldEmitAttribute = static (ref StructHtmlToken _, ReadOnlyMemory<char> _) => false,
                }
            );
            using var expected = expectedParser.ParseDocument(html);
            using var actual = CompactParser.Parse(
                html,
                attributeFilter: static (ref StructHtmlToken _, ReadOnlyMemory<char> _) => false
            );

            await Assert.That(actual.AttributeCount).IsEqualTo(0);
            await Assert.That(Snapshot(actual, 0)).IsEqualTo(Snapshot(expected));
        }
    }

    [Test]
    public async Task TinyCapacityHintsGrowIndependentPayloadAndAttributeArenas()
    {
        const string html =
            "<main id='m'><section class='a' data-x='1'><p title='p'>one</p><p title='q'>two</p></section></main>";
        var hints = new CompactParserHints
        {
            InitialNodeCapacity = 1,
            InitialPayloadCapacity = 1,
            InitialAttributeCapacity = 1,
        };

        using var expected = new HtmlParser(
            new HtmlParserOptions { SkipComments = true, SkipProcessingInstructions = true }
        ).ParseDocument(html);
        using var actual = CompactParser.Parse(html, hints: hints);

        await Assert.That(actual.AttributeCount).IsEqualTo(5);
        await Assert.That(Snapshot(actual, 0)).IsEqualTo(Snapshot(expected));
    }

    [Test]
    public async Task ProductionParserControlsFlowThroughDirectSession()
    {
        const string html =
            "<head><script>ignored()</script></head><body><div id='drop' class='keep'><p>x</p></div></body>";
        var parserOptions = new HtmlParserOptions
        {
            IsNotSupportingFrames = true,
            SkipScriptText = true,
            SkipComments = true,
            SkipProcessingInstructions = true,
            DisableElementPositionTracking = true,
            ShouldEmitAttribute = static (ref StructHtmlToken token, ReadOnlyMemory<char> name) =>
                token.Name == "div" && name.Span is "class",
        };
        var expectedFilter = new FirstTagAndAllChildren("body");
        var actualFilter = new FirstTagAndAllChildren("body");
        var expectedParser = new HtmlParser(parserOptions, ReadOnlyParser.DefaultContext);
        using var expected = expectedParser.ParseReadOnlyDocument(html.AsMemory(), expectedFilter.Loop);
        var session = new CompactParserSession(
            parserOptions: parserOptions,
            attributeFilter: static (ref StructHtmlToken token, ReadOnlyMemory<char> name) =>
                token.Name == "div" && name.Span is "class"
        );
        using var actual = session.Parse(html.AsMemory(), actualFilter.Loop);

        await Assert.That(actual.AttributeCount).IsEqualTo(1);
        await Assert.That(Snapshot(actual, 0)).IsEqualTo(Snapshot(expected));
    }

    [Test]
    public async Task RequestedLengthInputDoesNotParseUnusedTail()
    {
        const string retained = "<main><p>x</p></main>";
        var input = (retained + "<aside>unused</aside>").ToCharArray();
        using var expected = new HtmlParser(
            new HtmlParserOptions { SkipComments = true, SkipProcessingInstructions = true }
        ).ParseDocument(retained);
        using var actual = CompactParser.Parse(input, retained.Length);

        await Assert.That(Snapshot(actual, 0)).IsEqualTo(Snapshot(expected));
    }

    [Test]
    public async Task SubtreeMiddlewareDoesNotCountHtmlVoidElementsAsOpenScopes()
    {
        const string html = "<body><section id='target'><img><input><p>x</p></section><aside>tail</aside></body>";
        var expectedFilter = new OnlyElementWithIdAndDescendants("section", "target");
        var actualFilter = new OnlyElementWithIdAndDescendants("section", "target");
        var parserOptions = new HtmlParserOptions
        {
            SkipComments = true,
            SkipProcessingInstructions = true,
            ShouldEmitAttribute = static (ref StructHtmlToken _, ReadOnlyMemory<char> name) => name.Span is "id",
        };
        var expectedParser = new HtmlParser(parserOptions, ReadOnlyParser.DefaultContext);
        using var expected = expectedParser.ParseReadOnlyDocument(html, expectedFilter.Loop);
        var session = new CompactParserSession(parserOptions: parserOptions);
        using var actual = session.Parse(html, actualFilter.Loop);

        await Assert.That(Snapshot(actual, 0)).IsEqualTo(Snapshot(expected));
        await Assert.That(actual.FindNameId("aside")).IsEqualTo(ushort.MaxValue);
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

    private static string Snapshot(CompactDocument document, int handle)
    {
        var node = document.GetNode(handle);
        var result = $"{node.Kind}:{document.GetName(node.NameId)}[";
        if (node.PayloadIndex >= 0)
        {
            var payload = document.GetPayload(node.PayloadIndex);
            for (var i = 0; i < payload.AttributeCount; i++)
            {
                if (i != 0)
                    result += ",";
                var attribute = document.GetAttribute(payload.FirstAttribute + i);
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
