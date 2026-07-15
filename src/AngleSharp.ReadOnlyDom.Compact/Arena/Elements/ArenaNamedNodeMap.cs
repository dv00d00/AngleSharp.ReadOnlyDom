using AngleSharp.Common;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed class ArenaNamedNodeMap(Arena arena, int handle) : IConstructableNamedNodeMap
{
    internal Arena Arena => arena;
    internal int Handle => handle;

    public IConstructableAttr? this[StringOrMemory name] => arena.GetAttribute(handle, name);
    public int Length => arena.AttributeCount(handle);

    public bool SameAs(IConstructableNamedNodeMap? other)
    {
        if (other is null || Length != other.Length)
            return false;
        if (other is ArenaNamedNodeMap candidate && ReferenceEquals(arena, candidate.Arena))
            return arena.AttributesSame(handle, candidate.Handle);

        for (
            var attribute = arena.FirstAttributeHandle(handle);
            attribute >= 0;
            attribute = arena.NextAttribute(attribute)
        )
        {
            var match = other[arena.AttributeName(attribute)];
            if (match is null || match.Value != arena.AttributeValue(attribute))
                return false;
        }
        return true;
    }
}
