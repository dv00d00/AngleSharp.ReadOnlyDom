namespace AngleSharp.ReadOnlyDom.Streaming;

internal enum CompletedTextMode : byte
{
    None,
    Raw,
    Normalized,
}

internal enum AttributePredicateKind : byte
{
    Exists,
    Equals,
    ContainsToken,
}

internal readonly record struct CompiledAttributePredicate(int AttributeIndex, AttributePredicateKind Kind, byte[]? Value);

internal sealed record AttributePredicate(string Name, AttributePredicateKind Kind, string? Value);

internal readonly record struct QueryFrame(ulong TagHash, int TagLength, ulong Matches);

internal readonly record struct CompiledTagDispatch(ulong Hash, int Length, ulong CandidateBits);
