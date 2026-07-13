using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.CompactPrototype.Arena;

internal sealed class ArenaScriptElement(Arena arena, int handle)
    : ArenaElement(arena, handle),
        IConstructableScriptElement
{
    public Task RunAsync(CancellationToken cancel) => Task.CompletedTask;

    public bool Prepare(IConstructableDocument document) => false;
}
