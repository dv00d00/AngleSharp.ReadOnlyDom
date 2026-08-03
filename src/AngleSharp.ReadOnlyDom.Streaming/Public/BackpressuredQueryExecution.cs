using System.IO.Pipelines;

namespace AngleSharp.ReadOnlyDom.Streaming;

public static class BackpressuredQueryExecution
{
    public static async ValueTask<TState> ExecuteEncodedBackpressuredAsync<TState>(
        this QueryPlan<TState> plan,
        PipeReader reader,
        PipeWriter writer,
        HtmlInputEncoding inputEncoding,
        TState state,
        int flushThreshold = 16 * 1024,
        int inputSliceSize = 4 * 1024,
        CancellationToken cancellationToken = default,
        HtmlStreamingLimits? limits = null
    )
        where TState : class, IUtf8PublishSource
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(flushThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputSliceSize);

        limits ??= HtmlStreamingLimits.Default;
        using var execution = plan.CreateExecution(state, limits);
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
        await PublishAvailableAsync(writer, execution.State, cancellationToken).ConfigureAwait(false);
        return execution.State;

        ValueTask FlushIfNeededAsync() =>
            execution.State.PublishableUtf8.Length >= flushThreshold
                ? PublishAvailableAsync(writer, execution.State, cancellationToken)
                : ValueTask.CompletedTask;
    }

    public static async ValueTask<TState> ExecuteBackpressuredAsync<TState>(
        this QueryPlan<TState> plan,
        PipeReader reader,
        PipeWriter writer,
        TState state,
        int flushThreshold = 16 * 1024,
        int inputSliceSize = 4 * 1024,
        CancellationToken cancellationToken = default,
        HtmlStreamingLimits? limits = null
    )
        where TState : class, IUtf8PublishSource
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(flushThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputSliceSize);

        limits ??= HtmlStreamingLimits.Default;
        using var execution = plan.CreateExecution(state, limits);
        var tokenizer = new Utf8HtmlTokenizer(execution, limits);
        var input = new Utf8HtmlTokenizerInput(tokenizer, limits: limits);

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
                        if (execution.State.PublishableUtf8.Length >= flushThreshold)
                        {
                            await PublishAvailableAsync(writer, execution.State, cancellationToken)
                                .ConfigureAwait(false);
                        }
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
        await PublishAvailableAsync(writer, execution.State, cancellationToken).ConfigureAwait(false);

        return execution.State;
    }

    private static async ValueTask PublishAvailableAsync(
        PipeWriter writer,
        IUtf8PublishSource output,
        CancellationToken cancellationToken
    )
    {
        var publishable = output.PublishableUtf8;
        if (publishable.IsEmpty)
            return;

        publishable.Span.CopyTo(writer.GetSpan(publishable.Length));
        writer.Advance(publishable.Length);
        var result = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsCanceled)
            throw new OperationCanceledException(cancellationToken);
        if (result.IsCompleted)
            throw new IOException("The output completed before query execution finished.");
        output.AdvancePublished(publishable.Length);
    }
}
