using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed class ArenaSvgElement(Arena arena, int handle) : ArenaElement(arena, handle), IConstructableSvgElement;
