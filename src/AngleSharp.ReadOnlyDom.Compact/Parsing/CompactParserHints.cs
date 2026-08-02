namespace AngleSharp.ReadOnlyDom.Compact.Parsing;

/// <summary>
///     Initial capacities for the arena's pooled buffers. When the input length is known up front,
///     the parser scales these estimates with the content size; the values given here act as a
///     floor, never a ceiling, so a small hint does not cap a large document.
/// </summary>
internal sealed class CompactParserHints
{
    public int InitialNodeCapacity { get; init; } = 64;
    public int InitialPayloadCapacity { get; init; } = 32;
    public int InitialAttributeCapacity { get; init; } = 16;
    public int InitialTextCapacity { get; init; } = 256;
}
