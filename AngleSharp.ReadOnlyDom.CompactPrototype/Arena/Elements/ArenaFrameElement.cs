using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.CompactPrototype.Arena;

internal sealed class ArenaFrameElement(Arena arena, int handle)
    : ArenaElement(arena, handle),
        IConstructableFrameElement;
