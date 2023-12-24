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
}