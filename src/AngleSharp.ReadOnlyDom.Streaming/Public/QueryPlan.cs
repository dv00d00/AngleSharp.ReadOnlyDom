using System.IO.Pipelines;

using System.Buffers;
using System.Text;

namespace AngleSharp.ReadOnlyDom.Streaming;

public sealed class QueryPlan<TState>
{
    internal QueryPlan(
        QueryPlanNode<TState>[] nodes,
        string[] attributeNames,
        byte[][] attributeNamesUtf8,
        CompiledTagDispatch[] tagDispatch,
        QueryExplanation explanation
    )
    {
        Nodes = nodes;
        AttributeNames = attributeNames;
        AttributeNamesUtf8 = attributeNamesUtf8;
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
        Explanation = explanation;
    }

    internal QueryPlanNode<TState>[] Nodes { get; }
    internal string[] AttributeNames { get; }
    internal byte[][] AttributeNamesUtf8 { get; }
    internal CompiledTagDispatch[] TagDispatch { get; }
    internal ulong TextHandlerMask { get; }
    internal ulong CompletedHandlerMask { get; }
    internal ulong TerminalNodeMask { get; }

    public QueryExplanation Explanation { get; }

    /// <summary>Resolves the accumulated query state after successful input completion.</summary>
    public ResolvedQueryPlan<TState, TResult> Resolve<TResult>(Func<TState, TResult> resolver) => new(this, resolver);

    internal QueryExecution<TState> CreateExecution(TState state, HtmlStreamingLimits? limits = null) =>
        new(this, state, limits ?? HtmlStreamingLimits.Default);

    /// <summary>
    /// Executes the plan over complete, well-formed UTF-8. Use a streaming overload when arbitrary
    /// byte chunks require ingress validation and malformed-input replacement.
    /// </summary>
    public TState Execute(ReadOnlySpan<byte> utf8, TState state, HtmlStreamingLimits? limits = null)
    {
        limits ??= HtmlStreamingLimits.Default;
        using var execution = CreateExecution(state, limits);
        var tokenizer = new Utf8HtmlTokenizer(execution, limits);
        tokenizer.Write(utf8);
        tokenizer.Complete();
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
        ArgumentNullException.ThrowIfNull(handler);
        if (inputContract == Utf8InputContract.ArbitraryBytes && !System.Text.Unicode.Utf8.IsValid(utf8))
            throw new DecoderFallbackException("Query rewriting requires well-formed UTF-8 input.");
        if (inputContract is not (Utf8InputContract.ArbitraryBytes or Utf8InputContract.WellFormedUtf8))
            throw new ArgumentOutOfRangeException(nameof(inputContract));

        limits ??= HtmlStreamingLimits.Default;
        var collector = new Utf8RewriteCollector();
        using var execution = new QueryExecution<TState>(this, state, limits, handler, collector);
        var tokenizer = new Utf8HtmlTokenizer(execution, limits);
        tokenizer.Write(utf8);
        tokenizer.Complete();
        collector.WriteTo(utf8, output);
        return execution.State;
    }

    public async ValueTask<TState> ExecuteAsync(
        PipeReader reader,
        TState state,
        CancellationToken cancellationToken = default,
        HtmlStreamingLimits? limits = null
    )
    {
        limits ??= HtmlStreamingLimits.Default;
        using var execution = CreateExecution(state, limits);
        await Utf8HtmlTokenizer.TokenizeAsync(reader, execution, cancellationToken, limits).ConfigureAwait(false);
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
        using var execution = CreateExecution(state, limits);
        var tokenizer = new Utf8HtmlTokenizer(execution, limits);

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
                        tokenizer.Write(segment.Slice(offset, length));
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

        tokenizer.Complete();
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
        using var execution = CreateExecution(state, limits);
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
        using var execution = CreateExecution(state, limits);
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
