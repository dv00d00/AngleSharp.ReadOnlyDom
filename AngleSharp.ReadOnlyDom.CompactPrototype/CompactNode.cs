using AngleSharp.Dom;

namespace AngleSharp.ReadOnlyDom.CompactPrototype;

public enum CompactNodeKind : byte
{
    Document,
    Element,
    Text,
    Comment,
    ProcessingInstruction,
    Other,
}

public readonly struct CompactNode
{
    internal CompactNode(
        int firstChild,
        int nextSibling,
        int firstAttribute,
        int valueStart,
        int valueLength,
        ushort nameId,
        ushort attributeCount,
        NodeFlags flags,
        CompactNodeKind kind
    )
    {
        FirstChild = firstChild;
        NextSibling = nextSibling;
        FirstAttribute = firstAttribute;
        ValueStart = valueStart;
        ValueLength = valueLength;
        NameId = nameId;
        AttributeCount = attributeCount;
        Flags = flags;
        Kind = kind;
    }

    public int FirstChild { get; }
    public int NextSibling { get; }
    public int FirstAttribute { get; }
    public int ValueStart { get; }
    public int ValueLength { get; }
    public ushort NameId { get; }
    public ushort AttributeCount { get; }
    public NodeFlags Flags { get; }
    public CompactNodeKind Kind { get; }
}

public readonly struct CompactAttribute
{
    internal CompactAttribute(ushort nameId, int valueStart, int valueLength)
    {
        NameId = nameId;
        ValueStart = valueStart;
        ValueLength = valueLength;
    }

    public ushort NameId { get; }
    public int ValueStart { get; }
    public int ValueLength { get; }
}

public readonly record struct CompactSourceLocation(int Index, ushort Line, ushort Column);

[Flags]
public enum CompactMetadataOptions
{
    None = 0,
    ParentLinks = 1 << 0,
    SourceLocations = 1 << 1,
}
