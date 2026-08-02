using System.Runtime.InteropServices;

namespace AngleSharp.ReadOnlyDom.Compact.Document;

public enum CompactNodeKind : byte
{
    Document,
    Element,
    Text,
    Comment,
    ProcessingInstruction,
    Other
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct CompactNode
{
    internal CompactNode(
        int firstChild,
        int subtreeEndExclusive,
        int payloadIndex,
        ushort nameId,
        CompactNodeKind kind,
        byte flags
    )
    {
        FirstChild = firstChild;
        SubtreeEndExclusive = subtreeEndExclusive;
        PayloadIndex = payloadIndex;
        NameId = nameId;
        Kind = kind;
        Flags = flags;
    }

    public int FirstChild { get; }
    public int SubtreeEndExclusive { get; }
    public int PayloadIndex { get; }
    public ushort NameId { get; }
    public CompactNodeKind Kind { get; }
    public byte Flags { get; }
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct CompactNodePayload
{
    internal CompactNodePayload(int firstAttribute, int valueStart, int valueLength, ushort attributeCount)
    {
        FirstAttribute = firstAttribute;
        ValueStart = valueStart;
        ValueLength = valueLength;
        AttributeCount = attributeCount;
    }

    public int FirstAttribute { get; }
    public int ValueStart { get; }
    public int ValueLength { get; }
    public ushort AttributeCount { get; }
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

public readonly record struct CompactSourceLocation(int Index, int Line, int Column);

internal readonly record struct CompactTemplateBoundary(int Handle, int ContentStart, int ContentEnd);

[Flags]
public enum CompactMetadataOptions
{
    None = 0,
    ParentLinks = 1 << 0,
    SourceLocations = 1 << 1
}

public enum CompactDocumentLayout
{
    FrozenColumns,
    Packed
}