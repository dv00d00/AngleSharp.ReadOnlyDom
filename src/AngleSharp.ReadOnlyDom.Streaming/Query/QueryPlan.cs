using System.Buffers;
using AngleSharp.ReadOnlyDom.Streaming.Query.Execution;
using AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

namespace AngleSharp.ReadOnlyDom.Streaming.Query;

public sealed partial class QueryPlan<TState>
{
    internal QueryPlan(
        QueryPlanNode<TState>[] nodes,
        string[] attributeNames,
        byte[][] attributeNamesUtf8,
        CompiledNameIdentity[] attributeIdentities,
        ulong compactAttributeMask,
        CompiledTagDispatch[] tagDispatch
    )
    {
        Nodes = nodes;
        AttributeNames = attributeNames;
        AttributeNamesUtf8 = attributeNamesUtf8;
        AttributeIdentities = attributeIdentities;
        CompactAttributeMask = compactAttributeMask;
        TagDispatch = tagDispatch;
        TextHandlerMask = nodes.Aggregate(
            0UL,
            static (bits, node) => node.Text is null ? bits : bits | (1UL << node.Index)
        );
        CompletedHandlerMask = nodes.Aggregate(
            0UL,
            static (bits, node) => node.Completed is null ? bits : bits | (1UL << node.Index)
        );
        NormalizedTextHandlerMask = nodes.Aggregate(
            0UL,
            static (bits, node) =>
                node.Completed is null || node.CompletedTextMode != CompletedTextMode.Normalized
                    ? bits
                    : bits | (1UL << node.Index)
        );
        var parentMask = nodes.Aggregate(
            0UL,
            static (bits, node) => node.ParentIndex < 0 ? bits : bits | (1UL << node.ParentIndex)
        );
        var nodeMask = nodes.Length == 64 ? UInt64.MaxValue : (1UL << nodes.Length) - 1;
        TerminalNodeMask = nodeMask & ~parentMask;
    }

    internal QueryPlanNode<TState>[] Nodes { get; }
    internal string[] AttributeNames { get; }
    internal byte[][] AttributeNamesUtf8 { get; }
    internal CompiledNameIdentity[] AttributeIdentities { get; }
    internal ulong CompactAttributeMask { get; }
    internal CompiledTagDispatch[] TagDispatch { get; }
    internal ulong TextHandlerMask { get; }
    internal ulong CompletedHandlerMask { get; }

    /// <summary>
    /// Nodes capturing normalized text. Zero for every plan that never asks for it, which lets the
    /// execution skip word-boundary classification on the tag path entirely.
    /// </summary>
    internal ulong NormalizedTextHandlerMask { get; }
    internal ulong TerminalNodeMask { get; }

    internal QueryExecution<TState> CreateExecution(TState state, HtmlStreamingLimits? limits = null) =>
        new(this, state, limits ?? HtmlStreamingLimits.Default);

    internal QueryExecution<TState, TResourceLimits> CreateExecution<TResourceLimits>(
        TState state,
        HtmlStreamingLimits limits,
        RewriteHandler<TState>? rewriteHandler = null,
        TextRewriteHandler<TState>? textRewriteHandler = null,
        IHtmlRewriteCollector? rewriteCollector = null
    )
        where TResourceLimits : struct, IResourceLimitPolicy =>
        new(this, state, limits, rewriteHandler, textRewriteHandler, rewriteCollector);

    internal IQueryExecution<TState> CreateResourceAwareExecution(TState state, HtmlStreamingLimits limits) =>
        limits.EnforcesLimits
            ? new QueryExecution<TState>(this, state, limits)
            : new QueryExecution<TState, UnboundedResources>(this, state, limits);

    /// <summary>
    /// Begins a push-style streaming execution: call <see cref="StreamingQuerySession{TState}.Write"/> per input
    /// chunk and <see cref="StreamingQuerySession{TState}.Complete"/> at end of input. Select
    /// <see cref="Utf8InputContract.WellFormedUtf8"/> only when the complete input is guaranteed valid UTF-8.
    /// </summary>
    public StreamingQuerySession<TState> CreateSession(
        TState state,
        Utf8InputContract inputContract = Utf8InputContract.ArbitraryBytes,
        HtmlStreamingLimits? limits = null
    ) => new(this, state, inputContract, limits ?? HtmlStreamingLimits.Default);

    /// <summary>
    /// Begins a push-style streaming rewrite: call <see cref="StreamingRewriteSession{TState}.Write"/> per
    /// input chunk and <see cref="StreamingRewriteSession{TState}.Complete"/> at end of input. Bytes reach
    /// <paramref name="output"/> as soon as they can no longer be edited, so peak buffering is bounded by
    /// the largest open tag instead of the document size. Select
    /// <see cref="Utf8InputContract.WellFormedUtf8"/> only when the complete input is guaranteed valid
    /// UTF-8; the default repairs malformed sequences, publishing the normalized stream.
    /// </summary>
    public StreamingRewriteSession<TState> CreateRewriteSession(
        TState state,
        IBufferWriter<byte> output,
        RewriteHandler<TState> handler,
        Utf8InputContract inputContract = Utf8InputContract.ArbitraryBytes,
        HtmlStreamingLimits? limits = null
    ) => new(this, state, output, handler, inputContract, limits ?? HtmlStreamingLimits.Default);

    /// <summary>Begins a push-style streaming rewrite with element and/or raw text handlers.</summary>
    public StreamingRewriteSession<TState> CreateRewriteSession(
        TState state,
        IBufferWriter<byte> output,
        HtmlRewriteHandlers<TState> handlers,
        Utf8InputContract inputContract = Utf8InputContract.ArbitraryBytes,
        HtmlStreamingLimits? limits = null
    ) => new(this, state, output, handlers, inputContract, limits ?? HtmlStreamingLimits.Default);

    /// <summary>
    /// Streaming rewrite like
    /// <see cref="CreateRewriteSession(TState, IBufferWriter{byte}, RewriteHandler{TState}, Utf8InputContract, HtmlStreamingLimits?)"/>
    /// but publishes borrowed segments instead of copying into a writer: untouched runs of input
    /// reach <paramref name="sink"/> as slices of the session's current chunk or holdback buffer.
    /// </summary>
    public StreamingRewriteSession<TState> CreateRewriteSession(
        TState state,
        StreamingRewriteSegmentSink sink,
        RewriteHandler<TState> handler,
        Utf8InputContract inputContract = Utf8InputContract.ArbitraryBytes,
        HtmlStreamingLimits? limits = null
    ) => new(this, state, sink, handler, inputContract, limits ?? HtmlStreamingLimits.Default);

    /// <summary>Begins a borrowed-segment streaming rewrite with element and/or raw text handlers.</summary>
    public StreamingRewriteSession<TState> CreateRewriteSession(
        TState state,
        StreamingRewriteSegmentSink sink,
        HtmlRewriteHandlers<TState> handlers,
        Utf8InputContract inputContract = Utf8InputContract.ArbitraryBytes,
        HtmlStreamingLimits? limits = null
    ) => new(this, state, sink, handlers, inputContract, limits ?? HtmlStreamingLimits.Default);

    /// <summary>Executes the plan over UTF-8, replacing malformed input with U+FFFD.</summary>
    public TState Execute(ReadOnlySpan<byte> utf8, TState state, HtmlStreamingLimits? limits = null) =>
        Execute(utf8, state, Utf8InputContract.ArbitraryBytes, limits);

    /// <summary>
    /// Executes the plan with an explicit input contract. Select <see cref="Utf8InputContract.WellFormedUtf8"/>
    /// only when the complete input is guaranteed to be valid UTF-8.
    /// </summary>
    public TState Execute(
        ReadOnlySpan<byte> utf8,
        TState state,
        Utf8InputContract inputContract,
        HtmlStreamingLimits? limits = null
    )
    {
        if (inputContract is not (Utf8InputContract.ArbitraryBytes or Utf8InputContract.WellFormedUtf8))
            throw new ArgumentOutOfRangeException(nameof(inputContract));

        limits ??= HtmlStreamingLimits.Default;
        using var execution = CreateResourceAwareExecution(state, limits);
        if (inputContract == Utf8InputContract.WellFormedUtf8)
        {
            var tokenizer = Utf8HtmlTokenizerPipeline.CreateTokenizer(execution, limits);
            tokenizer.Write(utf8);
            tokenizer.Complete();
        }
        else
        {
            var input = Utf8HtmlTokenizerPipeline.CreateInput(execution, inputContract, limits);
            input.Write(utf8);
            input.Complete();
        }
        return execution.State;
    }
}
