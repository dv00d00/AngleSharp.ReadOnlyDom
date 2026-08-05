using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming.Input;
using AngleSharp.ReadOnlyDom.Streaming.Query.Execution;
using AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

namespace AngleSharp.ReadOnlyDom.Streaming.Query;

public sealed class QueryPlan<TState>
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
    internal ulong TerminalNodeMask { get; }

    internal QueryExecution<TState> CreateExecution(TState state, HtmlStreamingLimits? limits = null) =>
        new(this, state, limits ?? HtmlStreamingLimits.Default);

    internal QueryExecution<TState, TResourceLimits> CreateExecution<TResourceLimits>(
        TState state,
        HtmlStreamingLimits limits
    )
        where TResourceLimits : struct, IResourceLimitPolicy => new(this, state, limits);

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

    /// <summary>
    /// Rewrites matching terminal query nodes into <paramref name="output"/> while copying every untouched source
    /// byte verbatim. Arbitrary input is validated once before tokenization; callers that already guarantee valid
    /// UTF-8 can explicitly select <see cref="Utf8InputContract.WellFormedUtf8"/> to skip that pass.
    /// </summary>
    public TState Rewrite(
        ReadOnlySpan<byte> utf8,
        IBufferWriter<byte> output,
        TState state,
        RewriteHandler<TState> handler,
        Utf8InputContract inputContract = Utf8InputContract.ArbitraryBytes,
        HtmlStreamingLimits? limits = null
    )
    {
        ArgumentNullException.ThrowIfNull(output);
        using var execution = RewriteCore(utf8, state, handler, inputContract, limits, out var collector);
        collector.WriteTo(utf8, output);
        return execution.State;
    }

    /// <summary>
    /// Rewrites like <see cref="Rewrite(ReadOnlySpan{byte}, IBufferWriter{byte}, TState, RewriteHandler{TState}, Utf8InputContract, HtmlStreamingLimits?)"/>
    /// but publishes the result as borrowed segments instead of copying it: every untouched run of
    /// source bytes reaches <paramref name="sink"/> as a slice of <paramref name="utf8"/>.
    /// </summary>
    public TState Rewrite(
        ReadOnlySpan<byte> utf8,
        TState state,
        RewriteHandler<TState> handler,
        RewriteSegmentSink<TState> sink,
        Utf8InputContract inputContract = Utf8InputContract.ArbitraryBytes,
        HtmlStreamingLimits? limits = null
    )
    {
        ArgumentNullException.ThrowIfNull(sink);
        using var execution = RewriteCore(utf8, state, handler, inputContract, limits, out var collector);
        var finalState = execution.State;
        collector.WriteTo(utf8, ref finalState, sink);
        return finalState;
    }

    private IQueryExecution<TState> RewriteCore(
        ReadOnlySpan<byte> utf8,
        TState state,
        RewriteHandler<TState> handler,
        Utf8InputContract inputContract,
        HtmlStreamingLimits? limits,
        out Utf8RewriteCollector collector
    )
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (inputContract == Utf8InputContract.ArbitraryBytes && !System.Text.Unicode.Utf8.IsValid(utf8))
            throw new DecoderFallbackException("Query rewriting requires well-formed UTF-8 input.");
        if (inputContract is not (Utf8InputContract.ArbitraryBytes or Utf8InputContract.WellFormedUtf8))
            throw new ArgumentOutOfRangeException(nameof(inputContract));

        limits ??= HtmlStreamingLimits.Default;
        collector = new Utf8RewriteCollector();
        IQueryExecution<TState> execution = limits.EnforcesLimits
            ? new QueryExecution<TState>(this, state, limits, handler, collector)
            : new QueryExecution<TState, UnboundedResources>(this, state, limits, handler, collector);
        try
        {
            var tokenizer = Utf8HtmlTokenizerPipeline.CreateTokenizer(execution, limits);
            tokenizer.Write(utf8);
            tokenizer.Complete();
        }
        catch
        {
            execution.Dispose();
            throw;
        }
        return execution;
    }

    /// <summary>
    /// Executes the plan over streamed UTF-8. Select <see cref="Utf8InputContract.WellFormedUtf8"/> only when the
    /// complete input is guaranteed to be valid UTF-8; the default validates and repairs arbitrary bytes.
    /// </summary>
    public async ValueTask<TState> ExecuteAsync(
        PipeReader reader,
        TState state,
        CancellationToken cancellationToken = default,
        HtmlStreamingLimits? limits = null,
        Utf8InputContract inputContract = Utf8InputContract.ArbitraryBytes
    )
    {
        limits ??= HtmlStreamingLimits.Default;
        using var execution = CreateResourceAwareExecution(state, limits);
        await Utf8HtmlTokenizerPipeline
            .TokenizeAsync(reader, execution, cancellationToken, limits, inputContract)
            .ConfigureAwait(false);
        return execution.State;
    }

    /// <summary>
    /// Executes the plan while callbacks write directly to <paramref name="writer"/>, flushing between bounded input
    /// slices so downstream backpressure stops further input consumption.
    /// </summary>
    public async ValueTask<TState> ExecuteAsync(
        PipeReader reader,
        PipeWriter writer,
        TState state,
        int flushThreshold = 16 * 1024,
        int inputSliceSize = 4 * 1024,
        CancellationToken cancellationToken = default,
        HtmlStreamingLimits? limits = null
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(flushThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputSliceSize);

        limits ??= HtmlStreamingLimits.Default;
        using var execution = CreateResourceAwareExecution(state, limits);
        var input = Utf8HtmlTokenizerPipeline.CreateInput(execution, Utf8InputContract.ArbitraryBytes, limits);

        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (result.IsCanceled)
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
                throw new OperationCanceledException(cancellationToken);
            }
            try
            {
                foreach (var segment in buffer)
                {
                    for (var offset = 0; offset < segment.Length; offset += inputSliceSize)
                    {
                        var length = Math.Min(inputSliceSize, segment.Length - offset);
                        input.Write(segment.Slice(offset, length));
                        if (!writer.CanGetUnflushedBytes || writer.UnflushedBytes >= flushThreshold)
                            await FlushOutputAsync(writer, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                reader.AdvanceTo(buffer.End);
            }

            if (result.IsCompleted)
                break;
        }

        input.Complete();
        await FlushOutputAsync(writer, cancellationToken).ConfigureAwait(false);
        return execution.State;
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
        using var execution = CreateResourceAwareExecution(state, limits);
        await EncodedHtmlInput
            .TokenizeAsync(reader, inputEncoding, execution, cancellationToken, limits: limits)
            .ConfigureAwait(false);
        return execution.State;
    }

    /// <summary>
    /// Decodes input and executes the plan while callbacks write directly to <paramref name="writer"/>, preserving
    /// downstream backpressure between bounded input slices.
    /// </summary>
    public async ValueTask<TState> ExecuteEncodedAsync(
        PipeReader reader,
        PipeWriter writer,
        HtmlInputEncoding inputEncoding,
        TState state,
        int flushThreshold = 16 * 1024,
        int inputSliceSize = 4 * 1024,
        CancellationToken cancellationToken = default,
        HtmlStreamingLimits? limits = null
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(flushThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputSliceSize);

        limits ??= HtmlStreamingLimits.Default;
        using var execution = CreateResourceAwareExecution(state, limits);
        await EncodedHtmlInput
            .TokenizeAsync(
                reader,
                inputEncoding,
                execution,
                cancellationToken,
                inputSliceSize,
                FlushIfNeededAsync,
                limits
            )
            .ConfigureAwait(false);
        await FlushOutputAsync(writer, cancellationToken).ConfigureAwait(false);
        return execution.State;

        ValueTask FlushIfNeededAsync() =>
            !writer.CanGetUnflushedBytes || writer.UnflushedBytes >= flushThreshold
                ? FlushOutputAsync(writer, cancellationToken)
                : ValueTask.CompletedTask;
    }

    private static async ValueTask FlushOutputAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        if (writer.CanGetUnflushedBytes && writer.UnflushedBytes == 0)
            return;

        var result = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsCanceled)
            throw new OperationCanceledException(cancellationToken);
        if (result.IsCompleted)
            throw new IOException("The output completed before query execution finished.");
    }
}
