using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom.CompactPrototype.Arena;

internal struct MutableAttribute(StringOrMemory name, StringOrMemory value)
{
    public StringOrMemory Name = name;
    public StringOrMemory Value = value;
    public int Next = -1;
}
