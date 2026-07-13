namespace AngleSharp.ReadOnlyDom.CompactPrototype;

public sealed class CompactParserHints
{
    public int InitialNodeCapacity { get; init; } = 64;
    public int InitialPayloadCapacity { get; init; } = 32;
    public int InitialAttributeCapacity { get; init; } = 16;
    public int InitialTextCapacity { get; init; } = 256;
}
