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
    private readonly QueryExecution<TState> _execution;
    private readonly Utf8HtmlTokenizerInput _input;
    private bool _completed;

    internal StreamingQuerySession(
        QueryPlan<TState> plan,
        TState state,
        Utf8InputContract inputContract,
        HtmlStreamingLimits limits
    )
    {
        if (inputContract is not (Utf8InputContract.ArbitraryBytes or Utf8InputContract.WellFormedUtf8))
            throw new ArgumentOutOfRangeException(nameof(inputContract));

        _execution = plan.CreateExecution(state, limits);
        try
        {
            var tokenizer = new Utf8HtmlTokenizer(_execution, limits);
            _input = new Utf8HtmlTokenizerInput(tokenizer, inputContract, limits);
        }
        catch
        {
            _execution.Dispose();
            throw;
        }
    }

    /// <summary>Gets the state object; handlers may have mutated it after any <see cref="Write"/>.</summary>
    public TState State => _execution.State;

    /// <summary>Consumes the next input chunk. Chunk boundaries may split UTF-8 sequences freely.</summary>
    public void Write(ReadOnlySpan<byte> utf8) => _input.Write(utf8);

    /// <inheritdoc cref="Write(ReadOnlySpan{byte})"/>
    public void Write(ReadOnlyMemory<byte> utf8) => _input.Write(utf8);

    /// <summary>Signals end of input, flushing carried state, and returns the final state.</summary>
    public TState Complete()
    {
        if (!_completed)
        {
            _input.Complete();
            _completed = true;
        }
        return _execution.State;
    }

    /// <summary>Releases pooled buffers. Completing first is not required.</summary>
    public void Dispose() => _execution.Dispose();
}
