using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.Html.Model;

class ReadOnlyHtmlFormElement : ReadOnlyHtmlElement, IConstructableFormElement
{
    public ReadOnlyHtmlFormElement(ReadOnlyDocument? owner, StringOrMemory prefix = default)
        : base(owner, TagNames.Form, prefix, NodeFlags.Special) { }

    public override IConstructableNode ShallowCopy()
    {
        var readOnlyElement = new ReadOnlyHtmlFormElement(null, Prefix);
        PopulateAttributes(readOnlyElement);
        return readOnlyElement;
    }
}
