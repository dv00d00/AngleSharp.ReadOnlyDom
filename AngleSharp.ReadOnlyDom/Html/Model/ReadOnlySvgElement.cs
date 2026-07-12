using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.Html.Model;

class ReadOnlySvgElement : ReadOnlyHtmlElement, IConstructableSvgElement
{
    public ReadOnlySvgElement(
        ReadOnlyDocument? owner,
        StringOrMemory localName = default,
        StringOrMemory prefix = default,
        NodeFlags flags = NodeFlags.None
    )
        : base(owner, Combine(prefix, localName), localName, prefix, NamespaceNames.SvgUri, flags | NodeFlags.SvgMember)
    { }

    public override IConstructableNode ShallowCopy()
    {
        var readOnlyElement = new ReadOnlySvgElement(null, LocalName, Prefix, Flags);
        PopulateAttributes(readOnlyElement);
        return readOnlyElement;
    }
}
