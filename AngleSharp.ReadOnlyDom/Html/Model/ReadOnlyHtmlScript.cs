using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.Html.Model;

class ReadOnlyHtmlScript : ReadOnlyHtmlElement, IConstructableScriptElement
{
    public ReadOnlyHtmlScript(ReadOnlyDocument? owner, StringOrMemory prefix = default)
        : base(owner, TagNames.Script, prefix, NodeFlags.Special | NodeFlags.LiteralText) { }

    bool IConstructableScriptElement.Prepare(IConstructableDocument document) => false;

    Task IConstructableScriptElement.RunAsync(CancellationToken cancel) => Task.CompletedTask;

    public override IConstructableNode ShallowCopy()
    {
        var readOnlyHtmlScript = new ReadOnlyHtmlScript(null, Prefix);
        PopulateAttributes(readOnlyHtmlScript);
        return readOnlyHtmlScript;
    }
}
