namespace AngleSharp.ReadOnlyDom.Streaming.Query.Execution;

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

/// <summary>
/// <paramref name="TagIdentityLength"/> carries the word-boundary classification in its sign bit so
/// the close path does not repeat the lookup the open path already did, and the frame stays 32 bytes
/// on x64. A boundary name is always compact-representable, so the flag is only ever set on a zero
/// length; readers comparing the length must mask with <c>Int32.MaxValue</c>.
/// </summary>
internal readonly record struct QueryFrame(
    ulong TagIdentity,
    int TagIdentityLength,
    byte[]? FallbackTagNameUtf8,
    ulong Matches,
    int RewriteScopeId
);

internal readonly record struct CompiledTagDispatch(ulong Identity, int IdentityLength, ulong CandidateBits);

internal readonly record struct CompiledNameIdentity(ulong Value, int Length);
