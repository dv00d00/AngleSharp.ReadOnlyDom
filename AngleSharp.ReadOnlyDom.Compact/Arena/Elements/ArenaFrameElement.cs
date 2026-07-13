using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed class ArenaFrameElement(Arena arena, int handle)
    : ArenaElement(arena, handle),
        IConstructableFrameElement;
