using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed class ArenaFormElement(Arena arena, int handle)
    : ArenaElement(arena, handle),
        IConstructableFormElement;
