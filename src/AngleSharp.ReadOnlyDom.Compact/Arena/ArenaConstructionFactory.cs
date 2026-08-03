using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.ReadOnlyDom.Compact.Document;
using AngleSharp.ReadOnlyDom.Compact.Parsing;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed class ArenaConstructionFactory : IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>
{
    private readonly ICompactConstructionViewDefinition? _constructionView;
    private readonly CompactParserHints _hints;
    private readonly CompactDocumentLayout _layout;
    private readonly CompactMetadataOptions _options;
    private readonly bool _trackSourceReferences;

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

    public ArenaDocument CreateDocument(TextSource source, IBrowsingContext? context = null)
    {
        return new Arena(
            ScaledHints(source),
            _trackSourceReferences,
            _constructionView?.CreateState(source)
        ).CreateDocument(source, _options, _layout);
    }

    public ArenaHandle Create(
        ArenaDocument document,
        StringOrMemory localName,
        StringOrMemory prefix = default,
        NodeFlags flags = NodeFlags.None
    )
    {
        return new ArenaHandle(
            document.Arena,
            document.Arena.CreateElementHandle(localName, prefix, GetHtmlFlags(localName, flags))
        );
    }

    public ArenaHandle CreateNoScript(ArenaDocument document, bool scripting)
    {
        return CreateKnown(document, TagNames.NoScript);
    }

    public ArenaHandle CreateDocumentType(
        ArenaDocument document,
        StringOrMemory name,
        StringOrMemory publicIdentifier,
        StringOrMemory systemIdentifier
    )
    {
        return new ArenaHandle(document.Arena, document.Arena.CreateLeafHandle(name, default, CompactNodeKind.Other));
    }

    public ArenaHandle CreateMath(ArenaDocument document, StringOrMemory name = default)
    {
        return new ArenaHandle(document.Arena, document.Arena.CreateElementHandle(name, default, GetMathFlags(name)));
    }

    public ArenaHandle CreateSvg(ArenaDocument document, StringOrMemory name = default)
    {
        return new ArenaHandle(document.Arena, document.Arena.CreateElementHandle(name, default, GetSvgFlags(name)));
    }

    public ArenaHandle CreateMeta(ArenaDocument document)
    {
        return CreateKnown(document, TagNames.Meta);
    }

    public ArenaHandle CreateScript(ArenaDocument document, bool parserInserted, bool started)
    {
        return CreateKnown(document, TagNames.Script);
    }

    public ArenaHandle CreateFrame(ArenaDocument document)
    {
        return CreateKnown(document, TagNames.Frame);
    }

    public ArenaHandle CreateTemplate(ArenaDocument document)
    {
        return CreateKnown(document, TagNames.Template);
    }

    public ArenaHandle CreateForm(ArenaDocument document)
    {
        return CreateKnown(document, TagNames.Form);
    }

    public ArenaHandle CreateUnknown(ArenaDocument document, StringOrMemory tagName)
    {
        return CreateKnown(document, tagName);
    }

    public ArenaHandle GetDocumentNode(ArenaDocument document)
    {
        return new ArenaHandle(document.Arena, document.NodeHandle);
    }

    public ArenaHandle GetDocumentElement(ArenaDocument document)
    {
        for (var i = 0; i < document.Arena.ChildCount(document.NodeHandle); i++)
        {
            var handle = document.Arena.ChildAt(document.NodeHandle, i);
            if (document.Arena.Kind(handle) == CompactNodeKind.Element)
                return new ArenaHandle(document.Arena, handle);
        }

        return default;
    }

    public ArenaHandle GetHead(ArenaDocument document)
    {
        var root = GetDocumentElement(document);
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

    /// <summary>
    ///     Sizes the initial column capacities from the input length so typical documents avoid the
    ///     repeated grow-and-copy cycle. User-provided hints act as a floor, never a ceiling.
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

        static int Scale(int estimate, int hint)
        {
            return Math.Max(hint, Math.Min(estimate, 1 << 20));
        }
    }

    private static ArenaHandle CreateKnown(ArenaDocument document, StringOrMemory name)
    {
        return new ArenaHandle(document.Arena, document.Arena.CreateElementHandle(name, default, GetHtmlFlags(name)));
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
            flags |= NodeFlags.MathTip | NodeFlags.Special | NodeFlags.Scoped;
        else if (name.Equals(TagNames.AnnotationXml))
            flags |= NodeFlags.Special | NodeFlags.Scoped;
        return flags;
    }
}
