using AngleSharp.Common;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.CompactPrototype.Arena;

internal sealed class ArenaNamedNodeMap(Arena arena, int handle) : IConstructableNamedNodeMap
{
    public IConstructableAttr? this[StringOrMemory name] => arena.GetAttribute(handle, name);
    public int Length => arena.AttributeCount(handle);

    public bool SameAs(IConstructableNamedNodeMap? other) =>
        other is not null
        && Length == other.Length
        && arena.Attributes(handle).All(attribute => other[attribute.Name]?.Value == attribute.Value);
}
