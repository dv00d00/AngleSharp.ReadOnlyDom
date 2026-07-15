using System.IO.Pipelines;

namespace AngleSharp.ReadOnlyDom.Streaming.Utf8Stream.Query;

public sealed class QueryPlan<TState>
{
    internal QueryPlan(
        CompiledQueryNode<TState>[] nodes,
        string[] attributeNames,
        byte[][] attributeNameUtf8,
        CompiledTagDispatch[] tagDispatch,
        QueryExplanation explanation
    )
    {
        Nodes = nodes;
        AttributeNames = attributeNames;
        AttributeNameUtf8 = attributeNameUtf8;
        TagDispatch = tagDispatch;
        TextHandlerBits = nodes.Aggregate(
            0UL,
            static (bits, node) => node.Text is null ? bits : bits | (1UL << node.Index)
        );
        CompletedHandlerBits = nodes.Aggregate(
            0UL,
            static (bits, node) => node.Completed is null ? bits : bits | (1UL << node.Index)
        );
        Explanation = explanation;
    }

    internal CompiledQueryNode<TState>[] Nodes { get; }
    internal string[] AttributeNames { get; }
    internal byte[][] AttributeNameUtf8 { get; }
    internal CompiledTagDispatch[] TagDispatch { get; }
    internal ulong TextHandlerBits { get; }
    internal ulong CompletedHandlerBits { get; }

    public QueryExplanation Explanation { get; }

    public QuerySession<TState> CreateSession(TState state, HtmlStreamingLimits? limits = null) =>
        new(this, state, limits ?? HtmlStreamingLimits.Default);

    public TState Execute(ReadOnlySpan<byte> utf8, TState state, HtmlStreamingLimits? limits = null)
    {
        limits ??= HtmlStreamingLimits.Default;
        using var session = CreateSession(state, limits);
        var tokenizer = new Utf8HtmlTokenizer(session, limits);
        tokenizer.Write(utf8);
        tokenizer.Complete();
        return session.State;
    }

    public async ValueTask<TState> ExecuteAsync(
        PipeReader reader,
        TState state,
        CancellationToken cancellationToken = default,
        HtmlStreamingLimits? limits = null
    )
    {
        limits ??= HtmlStreamingLimits.Default;
        using var session = CreateSession(state, limits);
        await Utf8HtmlTokenizer
            .TokenizeAsync(reader, session, cancellationToken, limits)
            .ConfigureAwait(false);
        return session.State;
    }

    public async ValueTask<TState> ExecuteEncodedAsync(
        PipeReader reader,
        HtmlInputEncoding inputEncoding,
        TState state,
        CancellationToken cancellationToken = default,
        HtmlStreamingLimits? limits = null
    )
    {
        limits ??= HtmlStreamingLimits.Default;
        using var session = CreateSession(state, limits);
        await EncodedHtmlInput
            .TokenizeAsync(reader, inputEncoding, session, cancellationToken, limits: limits)
            .ConfigureAwait(false);
        return session.State;
    }
}
