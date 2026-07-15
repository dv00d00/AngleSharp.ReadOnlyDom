using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed class ArenaMetaElement(Arena arena, int handle) : ArenaElement(arena, handle), IConstructableMetaElement
{
    public void Handle() { }
}
