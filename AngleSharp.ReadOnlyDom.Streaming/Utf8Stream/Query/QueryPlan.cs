using System.IO.Pipelines;

namespace AngleSharp.ReadOnlyDom.Streaming.Utf8Stream.Query;

public sealed class QueryPlan<TState>
{
    internal QueryPlan(
        CompiledQueryNode<TState>[] nodes,
        string[] attributeNames,
        byte[][] attributeNameUtf8,
        QueryExplanation explanation
    )
    {
        Nodes = nodes;
        AttributeNames = attributeNames;
        AttributeNameUtf8 = attributeNameUtf8;
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
    internal ulong TextHandlerBits { get; }
    internal ulong CompletedHandlerBits { get; }

    public QueryExplanation Explanation { get; }

    public QuerySession<TState> CreateSession(TState state) => new(this, state);

    public TState Execute(ReadOnlySpan<byte> utf8, TState state)
    {
        using var session = CreateSession(state);
        var tokenizer = new Utf8HtmlTokenizer(session);
        tokenizer.Write(utf8);
        tokenizer.Complete();
        return session.State;
    }

    public async ValueTask<TState> ExecuteAsync(
        PipeReader reader,
        TState state,
        CancellationToken cancellationToken = default
    )
    {
        using var session = CreateSession(state);
        await Utf8HtmlTokenizer.TokenizeAsync(reader, session, cancellationToken).ConfigureAwait(false);
        return session.State;
    }
}