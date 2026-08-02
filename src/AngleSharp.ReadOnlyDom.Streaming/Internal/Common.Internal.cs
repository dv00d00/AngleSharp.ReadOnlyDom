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

internal readonly record struct CompiledAttributePredicate(
    int AttributeIndex,
    AttributePredicateKind Kind,
    byte[]? Value
);

internal sealed record AttributePredicate(string Name, AttributePredicateKind Kind, string? Value);

internal readonly record struct QueryFrame(
    ulong TagIdentity,
    int TagIdentityLength,
    byte[]? FallbackTagNameUtf8,
    ulong Matches
);

internal readonly record struct CompiledTagDispatch(ulong Identity, int IdentityLength, ulong CandidateBits);

internal readonly record struct CompiledNameIdentity(ulong Value, int Length);
