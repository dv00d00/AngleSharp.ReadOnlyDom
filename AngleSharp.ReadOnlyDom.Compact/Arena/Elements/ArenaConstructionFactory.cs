using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed class ArenaConstructionFactory : IDomConstructionElementFactory<ArenaDocument, ArenaElement>
{
    private readonly CompactParserHints _hints;
    private readonly bool _trackSourceReferences;
    private readonly CompactMetadataOptions _options;
    private readonly CompactDocumentLayout _layout;
    private readonly ICompactConstructionViewDefinition? _constructionView;

    public ArenaConstructionFactory(
        CompactParserHints hints,
        bool trackSourceReferences,
        CompactMetadataOptions options,
        CompactDocumentLayout layout,
        ICompactConstructionViewDefinition? constructionView = null
    )
    {
        _hints = hints;
        _trackSourceReferences = trackSourceReferences;
        _options = options;
        _layout = layout;
        _constructionView = constructionView;
    }

    public ArenaElement Create(
        ArenaDocument document,
        StringOrMemory localName,
        StringOrMemory prefix = default,
        NodeFlags flags = NodeFlags.None
    )
    {
        var canonical =
            localName.Memory.Span.IndexOf('-') >= 0 ? NodeFlags.None : GeneratedTagMetadata.GetFlags(localName);
        return document.Arena.CreateElement(
            localName,
            prefix,
            NamespaceNames.HtmlUri,
            flags | canonical | NodeFlags.HtmlMember
        );
    }

    public ArenaElement CreateNoScript(ArenaDocument document, bool scripting) => Create(document, TagNames.NoScript);

    public IConstructableNode CreateDocumentType(
        ArenaDocument document,
        StringOrMemory name,
        StringOrMemory publicIdentifier,
        StringOrMemory systemIdentifier
    ) => document.Arena.CreateLeaf(name, default, CompactNodeKind.Other);

    public IConstructableMathElement CreateMath(ArenaDocument document, StringOrMemory name = default)
    {
        var flags = NodeFlags.MathMember;
        if (
            name.Equals(TagNames.Mn)
            || name.Equals(TagNames.Mo)
            || name.Equals(TagNames.Mi)
            || name.Equals(TagNames.Ms)
            || name.Equals(TagNames.Mtext)
        )
        {
            flags |= NodeFlags.MathTip | NodeFlags.Special | NodeFlags.Scoped;
        }
        else if (name.Equals(TagNames.AnnotationXml))
        {
            flags |= NodeFlags.Special | NodeFlags.Scoped;
        }

        return (IConstructableMathElement)
            document.Arena.CreateElement(name, default, NamespaceNames.MathMlUri, flags, ElementMarker.Math);
    }

    public IConstructableSvgElement CreateSvg(ArenaDocument document, StringOrMemory name = default)
    {
        var flags = NodeFlags.SvgMember;
        if (name.Equals(TagNames.Desc) || name.Equals(TagNames.ForeignObject) || name.Equals(TagNames.Title))
            flags |= NodeFlags.HtmlTip | NodeFlags.Special | NodeFlags.Scoped;

        return (IConstructableSvgElement)
            document.Arena.CreateElement(name, default, NamespaceNames.SvgUri, flags, ElementMarker.Svg);
    }

    public IConstructableMetaElement CreateMeta(ArenaDocument document) =>
        (IConstructableMetaElement)CreateKnown(document, TagNames.Meta, ElementMarker.Meta);

    public IConstructableScriptElement CreateScript(ArenaDocument document, bool parserInserted, bool started) =>
        (IConstructableScriptElement)CreateKnown(document, TagNames.Script, ElementMarker.Script);

    public IConstructableFrameElement CreateFrame(ArenaDocument document) =>
        (IConstructableFrameElement)CreateKnown(document, TagNames.Frame, ElementMarker.Frame);

    public IConstructableTemplateElement CreateTemplate(ArenaDocument document) =>
        (IConstructableTemplateElement)CreateKnown(document, TagNames.Template, ElementMarker.Template);

    public IConstructableFormElement CreateForm(ArenaDocument document) =>
        (IConstructableFormElement)CreateKnown(document, TagNames.Form, ElementMarker.Form);

    public ArenaElement CreateUnknown(ArenaDocument document, StringOrMemory tagName) => Create(document, tagName);

    public ArenaDocument CreateDocument(TextSource source, IBrowsingContext? context = null) =>
        new Arena(
            _hints,
            _trackSourceReferences,
            _constructionView?.CreateState(source)
        ).CreateDocument(source, _options, _layout);

    private static ArenaElement CreateKnown(ArenaDocument document, StringOrMemory name, ElementMarker marker)
    {
        return document.Arena.CreateElement(
            name,
            default,
            NamespaceNames.HtmlUri,
            GeneratedTagMetadata.GetFlags(name) | NodeFlags.HtmlMember,
            marker
        );
    }
}
