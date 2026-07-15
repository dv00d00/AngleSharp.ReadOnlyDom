namespace AngleSharp.ReadOnlyDom.Streaming.Utf8Stream.Query;

public enum QueryRelation : byte
{
    Root,
    Descendant,
    Child,
}

public delegate void StartHandler<TState>(ref TState state, in Element element);

public delegate void TextHandler<TState>(ref TState state, ReadOnlySpan<byte> utf8);

public delegate void EndHandler<TState>(ref TState state);

public delegate void CompletedElementHandler<TState>(ref TState state, in CompletedElement element);

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

public static class StreamQuery
{
    public static QueryNode<TState> For<TState>(string rootTag) => QueryNode<TState>.Root(rootTag);
}

internal readonly record struct QueryFrame(ulong TagHash, int TagLength, ulong Matches);

public sealed record QueryExplanation(
    string ExecutionShape,
    IReadOnlyList<string> RequiredTags,
    IReadOnlyList<string> RequiredAttributes,
    int QueryNodes,
    int EstimatedFrameBytes,
    bool CanStopAfterRoot,
    string? FailureReason
);