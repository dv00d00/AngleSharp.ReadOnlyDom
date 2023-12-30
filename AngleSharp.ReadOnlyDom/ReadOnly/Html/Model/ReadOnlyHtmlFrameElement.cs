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
        if (_attributes != null)
            foreach (var attribute in _attributes)
                readOnlyElement.SetOwnAttribute(attribute.Name, attribute.Value);
        return readOnlyElement;
    }
}