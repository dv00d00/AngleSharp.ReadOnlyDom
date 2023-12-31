using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html.Model;

class ReadOnlyHtmlFrameElement : ReadOnlyHtmlElement, IConstructableFrameElement
{
    public ReadOnlyHtmlFrameElement(ReadOnlyDocument? owner, StringOrMemory prefix = default)
        : base(owner, TagNames.Frame, prefix, NodeFlags.SelfClosing)
    {
    }

    public IConstructableNode ShallowCopy()
    {
        var readOnlyElement = new ReadOnlyHtmlFrameElement(Owner);
        PopulateAttributes(readOnlyElement);
        return readOnlyElement;
    }
}