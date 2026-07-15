using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom;
using AngleSharp.ReadOnlyDom.Html;

namespace AngleSharp.Readonly.Tests;

public class MetadataTests
{
    [Test]
    [Arguments(ReadOnlyMetadataProfile.Minimal, false, false)]
    [Arguments(ReadOnlyMetadataProfile.Navigable, false, false)]
    [Arguments(ReadOnlyMetadataProfile.SourceMapped, true, false)]
    [Arguments(ReadOnlyMetadataProfile.Diagnostic, true, true)]
    public async Task PresetsAdvertiseTheirCapabilities(
        ReadOnlyMetadataProfile profile,
        bool sourceMapped,
        bool diagnostic
    )
    {
        using var document = ReadOnlyParser.CreateParser(profile).ParseReadOnlyDocument("<main>content</main>");

        await Assert.That(document.MetadataProfile).IsEqualTo(profile);
        await Assert.That(document.TryGetSourceMetadata(out _)).IsEqualTo(sourceMapped);
        await Assert.That(document.TryGetDiagnostics(out _)).IsEqualTo(diagnostic);
    }

    [Test]
    public async Task SourceMappedMetadataIsOwnedAndQueriedByTheDocument()
    {
        using var document = ReadOnlyParser
            .CreateParser(ReadOnlyMetadataProfile.SourceMapped)
            .ParseReadOnlyDocument("<main>content</main>");
        var main = (IReadOnlyElement)document.QueryOne(node => node.Tag("main"))!;

        var hasCapability = document.TryGetSourceMetadata(out var metadata);

        await Assert.That(hasCapability).IsTrue();
        await Assert.That(metadata.Fidelity).IsEqualTo(SourceFidelity.Positions);
        await Assert.That(metadata.TryGetSourceReference(main, out var source)).IsTrue();
        await Assert.That(source).IsNotNull();
    }

    [Test]
    public async Task DiagnosticDocumentOwnsRetainedErrors()
    {
        using var document = ReadOnlyParser
            .CreateParser(ReadOnlyMetadataProfile.Diagnostic)
            .ParseReadOnlyDocument("<main>content</main>");
        var concrete = (AngleSharp.ReadOnlyDom.Html.Model.ReadOnlyDocument)document;
        concrete.TrackError(new InvalidOperationException("test diagnostic"));

        var hasCapability = document.TryGetDiagnostics(out var diagnostics);

        await Assert.That(hasCapability).IsTrue();
        await Assert.That(diagnostics.Errors.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(diagnostics.Errors[^1].Message).IsEqualTo("test diagnostic");
    }

    [Test]
    public async Task DiagnosticRetainsParserErrorsWithoutChangingPermissiveConstruction()
    {
        using var document = ReadOnlyParser
            .CreateParser(ReadOnlyMetadataProfile.Diagnostic)
            .ParseReadOnlyDocument("<!DOCTYPE html PUBLIC><main>content</main>");

        var hasCapability = document.TryGetDiagnostics(out var diagnostics);

        await Assert.That(hasCapability).IsTrue();
        await Assert.That(diagnostics.Errors.Count).IsGreaterThan(0);
        await Assert.That(document.QueryOne(node => node.Tag("main"))).IsNotNull();
    }

    [Test]
    [Arguments(ReadOnlyMetadataProfile.Minimal, false, false)]
    [Arguments(ReadOnlyMetadataProfile.Navigable, true, false)]
    [Arguments(ReadOnlyMetadataProfile.SourceMapped, false, false)]
    [Arguments(ReadOnlyMetadataProfile.Diagnostic, true, true)]
    public async Task PresetsControlCommentsAndProcessingInstructions(
        ReadOnlyMetadataProfile profile,
        bool retainsComments,
        bool retainsProcessingInstructions
    )
    {
        using var document = ReadOnlyParser
            .CreateParser(profile)
            .ParseReadOnlyDocument("<!--note--><?xml-stylesheet href='theme.css'?><main>content</main>");

        var nodes = Descendants(document).ToArray();

        await Assert.That(nodes.Any(node => node is IReadOnlyCommentNode)).IsEqualTo(retainsComments);
        await Assert
            .That(nodes.Any(node => node is IReadOnlyProcessingInstructionNode))
            .IsEqualTo(retainsProcessingInstructions);
    }

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
    public async Task QualifiedNamesAreDerivedAcrossHtmlSvgAndMathMl()
    {
        var html = new AngleSharp.ReadOnlyDom.Html.Model.ReadOnlyHtmlElement(null, "main", "h");
        var svg = new AngleSharp.ReadOnlyDom.Html.Model.ReadOnlySvgElement(null, "circle", "s");
        var math = new AngleSharp.ReadOnlyDom.Html.Model.ReadOnlyMathElement(null, "mi", "m");

        await Assert.That(html.Prefix.ToString()).IsEqualTo("h");
        await Assert.That(html.LocalName.ToString()).IsEqualTo("main");
        await Assert.That(html.NamespaceUri).IsEqualTo(NamespaceNames.HtmlUri);
        await Assert.That(svg.Prefix.ToString()).IsEqualTo("s");
        await Assert.That(svg.LocalName.ToString()).IsEqualTo("circle");
        await Assert.That(svg.NamespaceUri).IsEqualTo(NamespaceNames.SvgUri);
        await Assert.That(math.Prefix.ToString()).IsEqualTo("m");
        await Assert.That(math.LocalName.ToString()).IsEqualTo("mi");
        await Assert.That(math.NamespaceUri).IsEqualTo(NamespaceNames.MathMlUri);
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

    private static IEnumerable<IReadOnlyNode> Descendants(IReadOnlyNode node)
    {
        foreach (var child in node.ChildNodes)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }
}
