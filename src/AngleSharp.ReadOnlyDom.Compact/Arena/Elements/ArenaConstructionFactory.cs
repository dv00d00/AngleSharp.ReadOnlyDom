using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed class ArenaConstructionFactory
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

    ArenaHandle IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>.Create(
        ArenaDocument document,
        StringOrMemory localName,
        StringOrMemory prefix,
        NodeFlags flags
    )
    {
        var handle = document.Arena.CreateElementHandle(
            localName,
            prefix,
            GetHtmlFlags(localName, flags)
        );
        return new ArenaHandle(document.Arena, handle);
    }

    ArenaHandle IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>.CreateNoScript(
        ArenaDocument document,
        bool scripting
    ) => CreateKnownHandle(document, TagNames.NoScript);

    ArenaHandle IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>.CreateDocumentType(
        ArenaDocument document,
        StringOrMemory name,
        StringOrMemory publicIdentifier,
        StringOrMemory systemIdentifier
    ) => new(document.Arena, document.Arena.CreateLeafHandle(name, default, CompactNodeKind.Other));

    ArenaHandle IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>.CreateMath(
        ArenaDocument document,
        StringOrMemory name
    ) => new(document.Arena, document.Arena.CreateElementHandle(name, default, GetMathFlags(name)));

    ArenaHandle IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>.CreateSvg(
        ArenaDocument document,
        StringOrMemory name
    ) => new(document.Arena, document.Arena.CreateElementHandle(name, default, GetSvgFlags(name)));

    ArenaHandle IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>.CreateMeta(ArenaDocument document) =>
        CreateKnownHandle(document, TagNames.Meta);

    ArenaHandle IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>.CreateScript(
        ArenaDocument document,
        bool parserInserted,
        bool started
    ) => CreateKnownHandle(document, TagNames.Script);

    ArenaHandle IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>.CreateFrame(ArenaDocument document) =>
        CreateKnownHandle(document, TagNames.Frame);

    ArenaHandle IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>.CreateTemplate(ArenaDocument document) =>
        CreateKnownHandle(document, TagNames.Template);

    ArenaHandle IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>.CreateForm(ArenaDocument document) =>
        CreateKnownHandle(document, TagNames.Form);

    ArenaHandle IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>.CreateUnknown(
        ArenaDocument document,
        StringOrMemory tagName
    ) => CreateKnownHandle(document, tagName);

    ArenaDocument IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>.CreateDocument(
        TextSource source,
        IBrowsingContext? context
    ) => new Arena(
        ScaledHints(source),
        _trackSourceReferences,
        _constructionView?.CreateState(source),
        materializeNodeWrappers: false
    ).CreateDocument(source, _options, _layout);

    ArenaHandle IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>.GetDocumentNode(ArenaDocument document) =>
        new(document.Arena, document.NodeHandle);

    ArenaHandle IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>.GetDocumentElement(ArenaDocument document)
    {
        for (var i = 0; i < document.Arena.ChildCount(document.NodeHandle); i++)
        {
            var handle = document.Arena.ChildAt(document.NodeHandle, i);
            if (document.Arena.Kind(handle) == CompactNodeKind.Element)
                return new ArenaHandle(document.Arena, handle);
        }
        return default;
    }

    ArenaHandle IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>.GetHead(ArenaDocument document)
    {
        var tree = (IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>)this;
        var root = tree.GetDocumentElement(document);
        if (root.IsNull)
            return default;
        for (var i = 0; i < root.ChildCount; i++)
        {
            var child = root.ChildAt(i);
            if (child.LocalName.Equals(TagNames.Head))
                return child;
        }
        return default;
    }

    private static ArenaHandle CreateKnownHandle(ArenaDocument document, StringOrMemory name)
    {
        var handle = document.Arena.CreateElementHandle(name, default, GetHtmlFlags(name));
        return new ArenaHandle(document.Arena, handle);
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
