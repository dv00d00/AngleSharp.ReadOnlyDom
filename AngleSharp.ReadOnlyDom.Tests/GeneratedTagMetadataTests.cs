using System.Reflection;
using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom;
using AngleSharp.ReadOnlyDom.Html.Model;

namespace AngleSharp.Readonly.Tests;

public class GeneratedTagMetadataTests
{
    [Test]
    public async Task GeneratedFlagsMatchCanonicalAngleSharpFactory()
    {
        var angleSharp = typeof(TagNames).Assembly;
        var factoryType = angleSharp.GetType("AngleSharp.Html.HtmlElementFactory", throwOnError: true)!;
        var factory = factoryType.GetField("Instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var create = factoryType.GetMethod("Create", BindingFlags.Instance | BindingFlags.Public)!;
        using var document = new HtmlParser().ParseDocument(string.Empty);
        var tags = typeof(TagNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string))
            .Select(field => (string)field.GetValue(null)!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal);

        foreach (var tag in tags)
        {
            var element = (IElement)create.Invoke(factory, [document, tag, null, NodeFlags.None])!;
            var expected = element.Flags & ~(NodeFlags.HtmlMember | NodeFlags.SvgMember | NodeFlags.MathMember);
            var actual = GeneratedTagMetadata.GetFlags(new StringOrMemory(tag));
            await Assert.That(actual).IsEqualTo(expected).Because($"tag '{tag}' must match AngleSharp");
        }
    }

    [Test]
    public async Task SpecializedHtmlElementsRemainSpecialized()
    {
        using var document = ReadOnlyParser
            .CreateParser(ReadOnlyMetadataProfile.Minimal)
            .ParseReadOnlyDocument("<form><input></form><template><p>x</p></template><script></script><meta>");

        await Assert.That(document.QueryOne(node => node.Tag("form"))).IsTypeOf<ReadOnlyHtmlFormElement>();
        await Assert.That(document.QueryOne(node => node.Tag("template"))).IsTypeOf<ReadOnlyHtmlTemplateElement>();
        await Assert.That(document.QueryOne(node => node.Tag("script"))).IsTypeOf<ReadOnlyHtmlScript>();
        await Assert.That(document.QueryOne(node => node.Tag("meta"))).IsTypeOf<ReadOnlyHtmlMeta>();
    }
}
