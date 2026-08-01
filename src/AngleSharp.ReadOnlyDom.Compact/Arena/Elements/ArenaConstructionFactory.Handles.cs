using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed partial class ArenaConstructionFactory
{
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
}
