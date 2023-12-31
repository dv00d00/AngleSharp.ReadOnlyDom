using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html.Model;

class ReadOnlyHtmlTemplateElement : ReadOnlyHtmlElement, IConstructableTemplateElement, IReadOnlyTemplateElement
{
    public ReadOnlyHtmlTemplateElement(ReadOnlyDocument? owner, StringOrMemory prefix = default)
        : base(owner, TagNames.Template, prefix, NodeFlags.Special | NodeFlags.Scoped | NodeFlags.HtmlTableScoped | NodeFlags.HtmlTableSectionScoped)
    {
    }

    private ReadOnlyNodeList Content { get; set; } = new ReadOnlyNodeList();
    IReadOnlyNodeList IReadOnlyTemplateElement.Content => Content;

    public void PopulateFragment()
    {
        while (ChildNodes.Length > 0)
        {
            var node = ChildNodes[0];
            RemoveNode(0, node);
            node.Parent = this;
            Content.Add(node);
        }
    }

    public IConstructableNode ShallowCopy()
    {
        var readOnlyElement = new ReadOnlyHtmlTemplateElement(Owner) { Content = Content };
        PopulateAttributes(readOnlyElement);
        return readOnlyElement;
    }
}