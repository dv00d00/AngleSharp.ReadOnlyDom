using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom;
using AngleSharp.ReadOnlyDom.Html;

namespace AngleSharp.Readonly.Tests;

public class MetadataTests
{
    [Test]
    public async Task NamespacesAreDerivedWithoutPerElementStorage()
    {
        var parser = new HtmlParser(default, ReadOnlyParser.DefaultContext);
        using var document = parser.ParseReadOnlyDocument("<svg><circle></circle></svg><math><mi>x</mi></math>");

        var html = (IReadOnlyElement)document.QueryOne(node => node.Tag("html"))!;
        var svg = (IReadOnlyElement)document.QueryOne(node => node.Tag("svg"))!;
        var circle = (IReadOnlyElement)document.QueryOne(node => node.Tag("circle"))!;
        var math = (IReadOnlyElement)document.QueryOne(node => node.Tag("math"))!;
        var mi = (IReadOnlyElement)document.QueryOne(node => node.Tag("mi"))!;

        await Assert.That(html.NamespaceUri).IsEqualTo(NamespaceNames.HtmlUri);
        await Assert.That(svg.NamespaceUri).IsEqualTo(NamespaceNames.SvgUri);
        await Assert.That(circle.NamespaceUri).IsEqualTo(NamespaceNames.SvgUri);
        await Assert.That(math.NamespaceUri).IsEqualTo(NamespaceNames.MathMlUri);
        await Assert.That(mi.NamespaceUri).IsEqualTo(NamespaceNames.MathMlUri);
    }

    [Test]
    public async Task SourceReferencesAreAllocatedOnlyWhenRequested()
    {
        var minimalParser = new HtmlParser(default, ReadOnlyParser.DefaultContext);
        using var minimalDocument = minimalParser.ParseReadOnlyDocument("<main>content</main>");
        var minimalMain = (IReadOnlyElement)minimalDocument.QueryOne(node => node.Tag("main"))!;

        var trackedParser = new HtmlParser(
            new HtmlParserOptions { IsKeepingSourceReferences = true },
            ReadOnlyParser.DefaultContext
        );
        using var trackedDocument = trackedParser.ParseReadOnlyDocument("<main>content</main>");
        var trackedMain = (IReadOnlyElement)trackedDocument.QueryOne(node => node.Tag("main"))!;

        await Assert.That(minimalMain.SourceReference).IsNull();
        await Assert.That(trackedMain.SourceReference).IsNotNull();
    }
}
