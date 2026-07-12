using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.ReadOnlyDom.Html.Model;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom;

internal interface IReadOnlyConstructionFactory : IDomConstructionElementFactory<ReadOnlyDocument, ReadOnlyHtmlElement>;

internal sealed class ReadOnlyDomConstructionFactory : IReadOnlyConstructionFactory
{
    private readonly ReadOnlyMetadataProfile _profile;

    public ReadOnlyDomConstructionFactory(ReadOnlyMetadataProfile profile)
    {
        _profile = profile;
    }

    public ReadOnlyHtmlElement Create(
        ReadOnlyDocument document,
        StringOrMemory localName,
        StringOrMemory prefix = default,
        NodeFlags flags = NodeFlags.None
    )
    {
        if (localName.Memory.Span.IndexOf('-') >= 0)
            return new ReadOnlyHtmlElement(document, localName, prefix, flags);

        var canonicalFlags = GeneratedTagMetadata.GetFlags(localName);
        var combinedFlags = flags | canonicalFlags;

        if (localName.Equals(TagNames.Form))
            return new ReadOnlyHtmlFormElement(document, prefix, combinedFlags);

        return new ReadOnlyHtmlElement(document, localName, prefix, combinedFlags);
    }

    public IConstructableMetaElement CreateMeta(ReadOnlyDocument document) => new ReadOnlyHtmlMeta(document);

    public IConstructableScriptElement CreateScript(ReadOnlyDocument document, bool parserInserted, bool started) =>
        new ReadOnlyHtmlScript(document);

    public IConstructableFrameElement CreateFrame(ReadOnlyDocument document) => new ReadOnlyHtmlFrameElement(document);

    public IConstructableTemplateElement CreateTemplate(ReadOnlyDocument document) =>
        new ReadOnlyHtmlTemplateElement(document);

    public IConstructableFormElement CreateForm(ReadOnlyDocument document) => new ReadOnlyHtmlFormElement(document);

    public ReadOnlyHtmlElement CreateNoScript(ReadOnlyDocument document, bool scripting) =>
        new(document, TagNames.NoScript, default, GeneratedTagMetadata.GetFlags(TagNames.NoScript));

    public IConstructableMathElement CreateMath(ReadOnlyDocument document, StringOrMemory name = default)
    {
        switch (name)
        {
            case var mn when mn.Equals(TagNames.Mn):
            case var mo when mo.Equals(TagNames.Mo):
            case var mi when mi.Equals(TagNames.Mi):
            case var ms when ms.Equals(TagNames.Ms):
            case var mtext when mtext.Equals(TagNames.Mtext):
                return new ReadOnlyMathElement(
                    document,
                    name,
                    default,
                    NodeFlags.MathTip | NodeFlags.Special | NodeFlags.Scoped
                );

            case var annotationXml when annotationXml.Equals(TagNames.AnnotationXml):
                return new ReadOnlyMathElement(document, name, default, NodeFlags.Special | NodeFlags.Scoped);

            default:
                return new ReadOnlyMathElement(document, name);
        }
    }

    public IConstructableSvgElement CreateSvg(ReadOnlyDocument document, StringOrMemory name = default)
    {
        switch (name)
        {
            case var desc when desc.Equals(TagNames.Desc):
            case var foreignObject when foreignObject.Equals(TagNames.ForeignObject):
            case var title when title.Equals(TagNames.Title):
                return new ReadOnlySvgElement(
                    document,
                    name,
                    default,
                    NodeFlags.HtmlTip | NodeFlags.Special | NodeFlags.Scoped
                );
            default:
                return new ReadOnlySvgElement(document, name);
        }
    }

    public ReadOnlyHtmlElement CreateUnknown(ReadOnlyDocument document, StringOrMemory tagName) =>
        new(document, tagName);

    public ReadOnlyDocument CreateDocument(TextSource source, IBrowsingContext? context = null) =>
        new(source, _profile);

    public IConstructableNode CreateDocumentType(
        ReadOnlyDocument document,
        StringOrMemory name,
        StringOrMemory publicIdentifier,
        StringOrMemory systemIdentifier
    )
    {
        return new ReadOnlyDocumentType(document, name)
        {
            SystemIdentifier = systemIdentifier,
            PublicIdentifier = publicIdentifier,
        };
    }
}
