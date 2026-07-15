using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed class ArenaMathElement(Arena arena, int handle)
    : ArenaElement(arena, handle),
        IConstructableMathElement;
