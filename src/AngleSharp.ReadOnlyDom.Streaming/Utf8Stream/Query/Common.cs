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

    /// <summary>
    /// Compiles independent query roots into one tokenizer pass, one structural stack, and one shared state value.
    /// </summary>
    public static QuerySet<TState> Observe<TState>(params QueryNode<TState>[] queries) => new(queries);
}

/// <summary>Independent observations that will be compiled into one query plan.</summary>
public sealed class QuerySet<TState>
{
    private readonly QueryNode<TState>[] _roots;

    internal QuerySet(QueryNode<TState>[] queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (queries.Length == 0)
            throw new ArgumentException("At least one query is required.", nameof(queries));

        var seen = new HashSet<QueryNode<TState>>(ReferenceEqualityComparer.Instance);
        var roots = new List<QueryNode<TState>>(queries.Length);
        foreach (var query in queries)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (seen.Add(query.RootNode))
                roots.Add(query.RootNode);
        }
        _roots = roots.ToArray();
    }

    public QueryPlan<TState> Compile() => QueryCompiler.Compile(_roots);

    /// <summary>Resolves the accumulated observation state after EOF closes all outstanding query scopes.</summary>
    public ResolvedQueryPlan<TState, TResult> Resolve<TResult>(Func<TState, TResult> resolver) =>
        Compile().Resolve(resolver);
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
