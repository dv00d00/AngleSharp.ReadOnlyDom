using System.IO.Pipelines;

namespace AngleSharp.ReadOnlyDom.Streaming.Query;

/// <summary>
/// Runs a compiled observation plan and resolves its accumulated state only after the input and all open query scopes
/// have completed. Provider errors, empty results, and successful values remain application-defined result shapes.
/// </summary>
public sealed class ResolvedQueryPlan<TState, TResult>
{
    private readonly QueryPlan<TState> _plan;
    private readonly Func<TState, TResult> _resolver;

    internal ResolvedQueryPlan(QueryPlan<TState> plan, Func<TState, TResult> resolver)
    {
        _plan = plan;
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public QueryExplanation Explanation => _plan.Explanation;

    public TResult Execute(ReadOnlySpan<byte> utf8, TState state, HtmlStreamingLimits? limits = null) =>
        _resolver(_plan.Execute(utf8, state, limits));

    public async ValueTask<TResult> ExecuteAsync(
        PipeReader reader,
        TState state,
        CancellationToken cancellationToken = default,
        HtmlStreamingLimits? limits = null
    )
    {
        var completed = await _plan.ExecuteAsync(reader, state, cancellationToken, limits).ConfigureAwait(false);
        return _resolver(completed);
    }

    public async ValueTask<TResult> ExecuteEncodedAsync(
        PipeReader reader,
        HtmlInputEncoding inputEncoding,
        TState state,
        CancellationToken cancellationToken = default,
        HtmlStreamingLimits? limits = null
    )
    {
        var completed = await _plan
            .ExecuteEncodedAsync(reader, inputEncoding, state, cancellationToken, limits)
            .ConfigureAwait(false);
        return _resolver(completed);
    }
}
