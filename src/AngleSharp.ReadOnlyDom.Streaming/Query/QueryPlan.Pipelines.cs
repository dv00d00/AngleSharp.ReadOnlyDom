using System.IO.Pipelines;
using AngleSharp.ReadOnlyDom.Streaming.Input;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

namespace AngleSharp.ReadOnlyDom.Streaming.Query;

public sealed partial class QueryPlan<TState>
{
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
