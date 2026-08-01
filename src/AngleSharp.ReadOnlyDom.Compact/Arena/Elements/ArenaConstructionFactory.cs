using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed partial class ArenaConstructionFactory
    : IDomConstructionElementFactory<ArenaDocument, ArenaElement>,
        IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>
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
        return document.Arena.CreateElement(localName, prefix, GetHtmlFlags(localName, flags));
    }

    public ArenaElement CreateNoScript(ArenaDocument document, bool scripting) => Create(document, TagNames.NoScript);

    public IConstructableNode CreateDocumentType(
        ArenaDocument document,
        StringOrMemory name,
        StringOrMemory publicIdentifier,
        StringOrMemory systemIdentifier
    ) => document.Arena.CreateLeaf(name, default, CompactNodeKind.Other);

    public IConstructableMathElement CreateMath(ArenaDocument document, StringOrMemory name = default)
        => (IConstructableMathElement)
            document.Arena.CreateElement(name, default, GetMathFlags(name), ElementMarker.Math);

    public IConstructableSvgElement CreateSvg(ArenaDocument document, StringOrMemory name = default)
        => (IConstructableSvgElement)
            document.Arena.CreateElement(name, default, GetSvgFlags(name), ElementMarker.Svg);

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
        new Arena(ScaledHints(source), _trackSourceReferences, _constructionView?.CreateState(source)).CreateDocument(
            source,
            _options,
            _layout
        );

    /// <summary>
    /// Sizes the initial column capacities from the input length so typical documents avoid the
    /// repeated grow-and-copy cycle. User-provided hints act as a floor, never a ceiling.
    /// </summary>
    private CompactParserHints ScaledHints(TextSource source)
    {
        if (!source.TryGetContentLength(out var length) || length <= 0)
            return _hints;
        return new CompactParserHints
        {
            InitialNodeCapacity = Scale(length / 32, _hints.InitialNodeCapacity),
            InitialPayloadCapacity = Scale(length / 48, _hints.InitialPayloadCapacity),
            InitialAttributeCapacity = Scale(length / 48, _hints.InitialAttributeCapacity),
            InitialTextCapacity = Scale(length / 2, _hints.InitialTextCapacity),
        };

        static int Scale(int estimate, int hint) => Math.Max(hint, Math.Min(estimate, 1 << 20));
    }

    private static ArenaElement CreateKnown(ArenaDocument document, StringOrMemory name, ElementMarker marker)
    {
        return document.Arena.CreateElement(name, default, GetHtmlFlags(name), marker);
    }

    private static NodeFlags GetHtmlFlags(StringOrMemory name, NodeFlags flags = NodeFlags.None)
    {
        var canonical = name.Memory.Span.IndexOf('-') >= 0 ? NodeFlags.None : GeneratedTagMetadata.GetFlags(name);
        return flags | canonical | NodeFlags.HtmlMember;
    }

    private static NodeFlags GetSvgFlags(StringOrMemory name)
    {
        var flags = NodeFlags.SvgMember;
        if (name.Equals(TagNames.Desc) || name.Equals(TagNames.ForeignObject) || name.Equals(TagNames.Title))
            flags |= NodeFlags.HtmlTip | NodeFlags.Special | NodeFlags.Scoped;
        return flags;
    }

    private static NodeFlags GetMathFlags(StringOrMemory name)
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
        return flags;
    }
}
