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

    bool IConstructableScriptElement.Prepare(IConstructableDocument document) => false;
    
    Task IConstructableScriptElement.RunAsync(CancellationToken cancel) => Task.CompletedTask;

    public IConstructableNode ShallowCopy()
    {
        var readOnlyHtmlScript = new ReadOnlyHtmlScript(Owner);
        PopulateAttributes(readOnlyHtmlScript);
        return readOnlyHtmlScript;
    }
}