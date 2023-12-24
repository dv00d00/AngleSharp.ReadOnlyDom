using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html.Model;

class ReadOnlyHtmlScript : ReadOnlyHtmlElement, IConstructableScriptElement
{
    public ReadOnlyHtmlScript(ReadOnlyDocument? owner, StringOrMemory prefix = default)
        :base(owner, TagNames.Script, prefix, NodeFlags.Special | NodeFlags.LiteralText)
    {
    }

    Boolean IConstructableScriptElement.Prepare(IConstructableDocument document) => false;
    
    Task IConstructableScriptElement.RunAsync(CancellationToken cancel) => Task.CompletedTask;

    public override IConstructableNode ShallowCopy()
    {
        var readOnlyElement = new ReadOnlyHtmlScript(Owner)
        {
            _childNodes = _childNodes
        };
        return readOnlyElement;
    }
}