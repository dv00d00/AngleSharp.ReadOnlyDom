using AngleSharp.ReadOnlyDom.Streaming.Query.Execution;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

namespace AngleSharp.ReadOnlyDom.Streaming.Query;

/// <summary>
/// A push-style streaming execution of a <see cref="QueryPlan{TState}"/>: feed input chunks with
/// <see cref="Write"/> as they arrive and finish with <see cref="Complete"/>. This is the synchronous
/// counterpart of the <see cref="System.IO.Pipelines.PipeReader"/> overloads for callers that already
/// hold bytes in hand; a direct <see cref="Write"/> call per chunk avoids the pipe's per-read
/// bookkeeping on hot paths.
/// </summary>
public sealed class StreamingQuerySession<TState> : IDisposable
{
    private readonly IQueryExecution<TState> _execution;
    private readonly IUtf8HtmlTokenizerInput _input;
    private bool _completed;
    private bool _disposed;

    internal StreamingQuerySession(
        QueryPlan<TState> plan,
        TState state,
        Utf8InputContract inputContract,
        HtmlStreamingLimits limits
    )
    {
        if (inputContract is not (Utf8InputContract.ArbitraryBytes or Utf8InputContract.WellFormedUtf8))
            throw new ArgumentOutOfRangeException(nameof(inputContract));

        if (limits.EnforcesLimits)
        {
            var execution = plan.CreateExecution<EnforcedResourceLimits>(state, limits);
            _execution = execution;
            try
            {
                var tokenizer = new Utf8HtmlTokenizer<EnforcedResourceLimits>(execution, limits);
                _input = new Utf8HtmlTokenizerInput<EnforcedResourceLimits>(tokenizer, inputContract, limits);
            }
            catch
            {
                execution.Dispose();
                throw;
            }
        }
        else
        {
            var execution = plan.CreateExecution<UnboundedResources>(state, limits);
            _execution = execution;
            try
            {
                var tokenizer = new Utf8HtmlTokenizer<UnboundedResources>(execution, limits);
                _input = new Utf8HtmlTokenizerInput<UnboundedResources>(tokenizer, inputContract, limits);
            }
            catch
            {
                execution.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Gets the state object; handlers may have mutated it after any <see cref="Write"/>. The caller-owned state remains
    /// available after this session is disposed.
    /// </summary>
    public TState State => _execution.State;

    /// <summary>Consumes the next input chunk. Chunk boundaries may split UTF-8 sequences freely.</summary>
    public void Write(ReadOnlySpan<byte> utf8)
    {
        ThrowIfDisposed();
        _input.Write(utf8);
    }

    /// <inheritdoc cref="Write(ReadOnlySpan{byte})"/>
    public void Write(ReadOnlyMemory<byte> utf8)
    {
        ThrowIfDisposed();
        _input.Write(utf8);
    }

    /// <inheritdoc cref="Write(ReadOnlySpan{byte})"/>
    public void Write(byte[] utf8)
    {
        ArgumentNullException.ThrowIfNull(utf8);
        Write(utf8.AsSpan());
    }

    /// <summary>Signals end of input, flushing carried state, and returns the final state.</summary>
    public TState Complete()
    {
        ThrowIfDisposed();
        if (!_completed)
        {
            _input.Complete();
            _completed = true;
        }
        return State;
    }

    /// <summary>Releases pooled buffers. Completing first is not required.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _execution.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
