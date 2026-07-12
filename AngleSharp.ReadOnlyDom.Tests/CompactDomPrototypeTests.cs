#if NET10_0
using System.Runtime.CompilerServices;
using AngleSharp.ReadOnlyDom;
using AngleSharp.ReadOnlyDom.CompactPrototype;

namespace AngleSharp.Readonly.Tests;

public class CompactDomPrototypeTests
{
    [Test]
    public async Task BuildsContiguousStructureWithoutOptionalMetadata()
    {
        using var source = ReadOnlyParser
            .CreateParser(ReadOnlyMetadataProfile.Minimal)
            .ParseReadOnlyDocument("<main id='root'><p>Hello <b>world</b></p></main>");

        var compact = CompactDomBuilder.Build(source);
        var main = compact.FindElements("main").Single();
        var paragraph = compact.FindElements("p").Single();
        var expected = Count(source);
        var mainNode = compact.GetNode(main);
        var id = compact.GetAttribute(mainNode.FirstAttribute);

        await Assert.That(compact.NodeCount).IsEqualTo(expected.Nodes);
        await Assert.That(compact.AttributeCount).IsEqualTo(expected.Attributes);
        await Assert.That(compact.HasParentLinks).IsFalse();
        await Assert.That(compact.HasSourceLocations).IsFalse();
        await Assert.That(compact.Children(main)).Contains(paragraph);
        await Assert.That(compact.GetName(id.NameId)).IsEqualTo("id");
        await Assert.That(compact.GetValue(id.ValueStart, id.ValueLength).ToString()).IsEqualTo("root");
        await Assert.That(Unsafe.SizeOf<CompactNode>()).IsLessThanOrEqualTo(32);
        await Assert.That(Unsafe.SizeOf<CompactAttribute>()).IsLessThanOrEqualTo(12);
    }

    [Test]
    [Arguments(CompactIndexMode.Dense)]
    [Arguments(CompactIndexMode.Sparse)]
    [Arguments(CompactIndexMode.Dictionary)]
    public async Task SourceLocationsSupportSelectableIndexModes(CompactIndexMode mode)
    {
        using var source = ReadOnlyParser
            .CreateParser(ReadOnlyMetadataProfile.SourceMapped)
            .ParseReadOnlyDocument("<main><p>content</p></main>");
        using var compact = CompactDomBuilder.Build(source, new CompactDomOptions { SourceLocationIndexMode = mode });

        var main = compact.FindElements("main").Single();
        await Assert.That(compact.SourceLocationIndexMode).IsEqualTo(mode);
        await Assert.That(compact.TryGetSourceLocation(main, out var location)).IsTrue();
        await Assert.That(location.Index).IsGreaterThanOrEqualTo(0);
        await Assert.That(compact.TryGetSourceLocation(0, out _)).IsFalse();
    }

    [Test]
    public async Task OptionalArraysEnableNavigationAndCompactSourceLocations()
    {
        using var source = ReadOnlyParser
            .CreateParser(ReadOnlyMetadataProfile.SourceMapped)
            .ParseReadOnlyDocument("<main><p>content</p></main>");

        var compact = CompactDomBuilder.Build(
            source,
            CompactMetadataOptions.ParentLinks | CompactMetadataOptions.SourceLocations
        );
        var main = compact.FindElements("main").Single();
        var paragraph = compact.FindElements("p").Single();

        await Assert.That(compact.HasParentLinks).IsTrue();
        await Assert.That(compact.GetParent(paragraph)).IsEqualTo(main);
        await Assert.That(compact.TryGetSourceLocation(main, out var location)).IsTrue();
        await Assert.That(location.Index).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task WrappersAreMaterializedOnlyOnRequest()
    {
        using var source = ReadOnlyParser
            .CreateParser(ReadOnlyMetadataProfile.Minimal)
            .ParseReadOnlyDocument("<main><p>one</p><p>two</p></main>");
        var compact = CompactDomBuilder.Build(source);

        var wrapper = compact.MaterializeWrapperTree();

        await Assert.That(Count(wrapper)).IsEqualTo(compact.NodeCount);
    }

    private static int Count(CompactNodeWrapper node) => 1 + node.Children.Sum(Count);

    private static (int Nodes, int Attributes) Count(AngleSharp.ReadOnlyDom.Html.IReadOnlyNode node)
    {
        var result = (
            Nodes: 1,
            Attributes: node is AngleSharp.ReadOnlyDom.Html.IReadOnlyElement element ? element.Attributes.Length : 0
        );
        var children = node is AngleSharp.ReadOnlyDom.Html.IReadOnlyTemplateElement template
            ? template.Content
            : node.ChildNodes;
        foreach (var child in children)
        {
            var childCount = Count(child);
            result.Nodes += childCount.Nodes;
            result.Attributes += childCount.Attributes;
        }

        return result;
    }
}
#endif
