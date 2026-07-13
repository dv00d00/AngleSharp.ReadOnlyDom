using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal struct MutableAttribute(ushort nameId, StringOrMemory value)
{
    public ushort NameId = nameId;
    public StringOrMemory Value = value;
    public int Next = -1;
}
