using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.CompactPrototype.Arena;

internal sealed class ArenaSvgElement(Arena arena, int handle) : ArenaElement(arena, handle), IConstructableSvgElement;
