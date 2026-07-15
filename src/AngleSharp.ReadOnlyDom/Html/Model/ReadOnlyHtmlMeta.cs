using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.Html.Model;

class ReadOnlyHtmlMeta : ReadOnlyHtmlElement, IConstructableMetaElement
{
    public ReadOnlyHtmlMeta(ReadOnlyDocument? owner, StringOrMemory prefix = default)
        : base(owner, TagNames.Meta, prefix, NodeFlags.Special | NodeFlags.SelfClosing) { }

    public void Handle()
    {
        // do nothing
    }

    public override IConstructableNode ShallowCopy()
    {
        var readOnlyElement = new ReadOnlyHtmlMeta(null, Prefix);
        PopulateAttributes(readOnlyElement);
        return readOnlyElement;
    }
}
