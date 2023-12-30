using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html.Model;

class ReadOnlyHtmlFormElement : ReadOnlyHtmlElement, IConstructableFormElement
{
    public ReadOnlyHtmlFormElement(ReadOnlyDocument? owner, StringOrMemory prefix = default)
        : base(owner, TagNames.Form, prefix, NodeFlags.Special)
    {
    }
    
    public IConstructableNode ShallowCopy()
    {
        var readOnlyElement = new ReadOnlyHtmlFormElement(Owner, default);
        
        if (_attributes != null)
            foreach (var attribute in _attributes)
                readOnlyElement.SetOwnAttribute(attribute.Name, attribute.Value);
       
        return readOnlyElement;
    }
}