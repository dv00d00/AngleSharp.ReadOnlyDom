using System.IO.Pipelines;
using AngleSharp.ReadOnlyDom.Streaming.Utf8Stream.Query;

namespace AngleSharp.ReadOnlyDom.Streaming.Utf8Stream;

public static class BackpressuredQueryExecution
{
    public static async ValueTask<TState> ExecuteBackpressuredAsync<TState>(
        this QueryPlan<TState> plan,
        PipeReader reader,
        PipeWriter writer,
        TState state,
        int flushThreshold = 16 * 1024,
        int inputSliceSize = 4 * 1024,
        CancellationToken cancellationToken = default
    )
        where TState : class, ICommittedUtf8Output
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(flushThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputSliceSize);

        using var session = plan.CreateSession(state);
        var tokenizer = new Utf8HtmlTokenizer(session);

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
                        if (state.CommittedUtf8.Length >= flushThreshold)
                        {
                            await FlushCommittedAsync(writer, state, cancellationToken).ConfigureAwait(false);
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

        tokenizer.Complete();
        await FlushCommittedAsync(writer, state, cancellationToken).ConfigureAwait(false);

        return session.State;
    }

    private static async ValueTask FlushCommittedAsync(
        PipeWriter writer,
        ICommittedUtf8Output output,
        CancellationToken cancellationToken
    )
    {
        var committed = output.CommittedUtf8;
        if (committed.IsEmpty)
            return;

        committed.Span.CopyTo(writer.GetSpan(committed.Length));
        writer.Advance(committed.Length);
        var result = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsCanceled)
            throw new OperationCanceledException(cancellationToken);
        if (result.IsCompleted)
            throw new IOException("The output completed before query execution finished.");
        output.AdvanceCommitted(committed.Length);
    }
}
