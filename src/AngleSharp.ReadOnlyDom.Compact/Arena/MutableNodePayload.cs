using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal struct MutableNodePayload
{
    public MutableNodePayload()
    {
        FirstAttribute = -1;
        LastAttribute = -1;
    }

    public StringOrMemory Value;
    public int FirstAttribute;
    public int LastAttribute;
    public ushort AttributeCount;
}
