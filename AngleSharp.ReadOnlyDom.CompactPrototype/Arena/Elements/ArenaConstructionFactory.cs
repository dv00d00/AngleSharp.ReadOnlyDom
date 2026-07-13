using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.CompactPrototype.Arena;

internal sealed class ArenaConstructionFactory : IDomConstructionElementFactory<ArenaDocument, ArenaElement>
{
    private readonly CompactParserHints _hints;
    private readonly bool _trackSourceReferences;

    public ArenaConstructionFactory(CompactParserHints hints, bool trackSourceReferences)
    {
        _hints = hints;
        _trackSourceReferences = trackSourceReferences;
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

    public IConstructableMathElement CreateMath(ArenaDocument document, StringOrMemory name = default) =>
        (IConstructableMathElement)
            document.Arena.CreateElement(
                name,
                default,
                NamespaceNames.MathMlUri,
                NodeFlags.MathMember,
                ElementMarker.Math
            );

    public IConstructableSvgElement CreateSvg(ArenaDocument document, StringOrMemory name = default) =>
        (IConstructableSvgElement)
            document.Arena.CreateElement(name, default, NamespaceNames.SvgUri, NodeFlags.SvgMember, ElementMarker.Svg);

    public IConstructableMetaElement CreateMeta(ArenaDocument document) =>
        (IConstructableMetaElement)
            document.Arena.CreateElement(
                TagNames.Meta,
                default,
                NamespaceNames.HtmlUri,
                GeneratedTagMetadata.GetFlags(TagNames.Meta) | NodeFlags.HtmlMember,
                ElementMarker.Meta
            );

    public IConstructableScriptElement CreateScript(ArenaDocument document, bool parserInserted, bool started) =>
        (IConstructableScriptElement)
            document.Arena.CreateElement(
                TagNames.Script,
                default,
                NamespaceNames.HtmlUri,
                GeneratedTagMetadata.GetFlags(TagNames.Script) | NodeFlags.HtmlMember,
                ElementMarker.Script
            );

    public IConstructableFrameElement CreateFrame(ArenaDocument document) =>
        (IConstructableFrameElement)
            document.Arena.CreateElement(
                TagNames.Frame,
                default,
                NamespaceNames.HtmlUri,
                GeneratedTagMetadata.GetFlags(TagNames.Frame) | NodeFlags.HtmlMember,
                ElementMarker.Frame
            );

    public IConstructableTemplateElement CreateTemplate(ArenaDocument document) =>
        (IConstructableTemplateElement)
            document.Arena.CreateElement(
                TagNames.Template,
                default,
                NamespaceNames.HtmlUri,
                GeneratedTagMetadata.GetFlags(TagNames.Template) | NodeFlags.HtmlMember,
                ElementMarker.Template
            );

    public IConstructableFormElement CreateForm(ArenaDocument document) =>
        (IConstructableFormElement)
            document.Arena.CreateElement(
                TagNames.Form,
                default,
                NamespaceNames.HtmlUri,
                GeneratedTagMetadata.GetFlags(TagNames.Form) | NodeFlags.HtmlMember,
                ElementMarker.Form
            );

    public ArenaElement CreateUnknown(ArenaDocument document, StringOrMemory tagName) => Create(document, tagName);

    public ArenaDocument CreateDocument(TextSource source, IBrowsingContext? context = null) =>
        new Arena(_hints, _trackSourceReferences).CreateDocument(source);
}
