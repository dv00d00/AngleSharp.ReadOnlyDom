using AngleSharp.Common;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed class ArenaAttribute(Arena arena, int handle) : IConstructableAttr
{
    internal int NodeHandle => handle;

    public StringOrMemory Name => arena.AttributeName(handle);
    public StringOrMemory Value
    {
        get => arena.AttributeValue(handle);
        set => arena.SetAttributeValue(handle, value);
    }
}
