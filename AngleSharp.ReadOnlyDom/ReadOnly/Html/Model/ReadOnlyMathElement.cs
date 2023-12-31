using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html.Model;

class ReadOnlyMathElement : ReadOnlyHtmlElement, IConstructableMathElement
{
    public ReadOnlyMathElement(ReadOnlyDocument? owner, StringOrMemory localName = default, StringOrMemory prefix = default, NodeFlags flags = NodeFlags.None)
        : base(owner, Combine(prefix, localName), localName, prefix, NamespaceNames.MathMlUri, flags | NodeFlags.MathMember)
    {
    }

    public override IConstructableNode ShallowCopy()
    {
        var readOnlyElement = new ReadOnlySvgElement(Owner, LocalName, prefix: default, Flags);
        PopulateAttributes(readOnlyElement);
        return readOnlyElement;
    }
}