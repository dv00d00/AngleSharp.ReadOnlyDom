using AngleSharp.Dom;
using AngleSharp.ReadOnlyDom.Html;
using AngleSharp.ReadOnlyDom.Html.Model;

namespace AngleSharp.Readonly.Tests;

public class ShallowCopyTests
{
    [Test]
    public async Task MathCopyPreservesMathTypeAndNamespace()
    {
        var original = new ReadOnlyMathElement(null, "mi");
        var copy = original.ShallowCopy();

        await Assert.That(copy).IsTypeOf<ReadOnlyMathElement>();
        await Assert.That(((IReadOnlyElement)copy).NamespaceUri).IsEqualTo(NamespaceNames.MathMlUri);
    }

    [Test]
    public async Task ShallowCopyDoesNotShareMutableAttributes()
    {
        var original = new ReadOnlyHtmlElement(null, "div");
        original.SetAttribute(null, "id", "before");

        var copy = (ReadOnlyHtmlElement)original.ShallowCopy();
        original.SetAttribute(null, "id", "after");

        await Assert.That(copy.GetAttribute(default, "id").ToString()).IsEqualTo("before");
        await Assert.That(original.GetAttribute(default, "id").ToString()).IsEqualTo("after");
    }

    [Test]
    public async Task TemplateCopyStartsWithIndependentEmptyContent()
    {
        var original = new ReadOnlyHtmlTemplateElement(null);
        original.AddNode(new ReadOnlyHtmlElement(null, "section"));
        original.PopulateFragment();

        var copy = (IReadOnlyTemplateElement)original.ShallowCopy();
        var originalView = (IReadOnlyTemplateElement)original;

        await Assert.That(originalView.Content.Length).IsEqualTo(1);
        await Assert.That(copy.Content.Length).IsEqualTo(0);
    }
}
