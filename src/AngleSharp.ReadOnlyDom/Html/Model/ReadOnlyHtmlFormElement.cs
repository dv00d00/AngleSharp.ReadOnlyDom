using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.Html.Model;

class ReadOnlyHtmlFormElement : ReadOnlyHtmlElement, IConstructableFormElement
{
    public ReadOnlyHtmlFormElement(
        ReadOnlyDocument? owner,
        StringOrMemory prefix = default,
        NodeFlags flags = NodeFlags.None
    )
        : base(owner, TagNames.Form, prefix, flags | NodeFlags.Special) { }

    public override IConstructableNode ShallowCopy()
    {
        var readOnlyElement = new ReadOnlyHtmlFormElement(null, Prefix, Flags);
        PopulateAttributes(readOnlyElement);
        return readOnlyElement;
    }
}
