using System.Buffers;
using AngleSharp.ReadOnlyDom.Streaming.Query.Execution;
using AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

namespace AngleSharp.ReadOnlyDom.Streaming.Query;

/// <summary>
/// A push-style streaming rewrite: feed input chunks with <see cref="Write"/> as they arrive and
/// finish with <see cref="Complete"/>. Every byte is published to the output as soon as it can no
/// longer be touched by a start-tag edit, so only the currently open start tag is ever buffered -
/// peak memory is independent of document size, bounded by
/// <see cref="HtmlStreamingLimits.MaximumBufferedTokenBytes"/> plus the caller's chunk size.
/// Output is byte-identical to
/// <see cref="QueryPlan{TState}.Rewrite(ReadOnlySpan{byte}, IBufferWriter{byte}, TState, RewriteHandler{TState}, Utf8InputContract, HtmlStreamingLimits?)"/>
/// for well-formed input regardless of how the input is chunked. Unlike the whole-buffer overloads,
/// which reject malformed UTF-8, <see cref="Utf8InputContract.ArbitraryBytes"/> input is repaired
/// in place: the published document is the normalized stream, with malformed sequences replaced by
/// U+FFFD.
/// </summary>
public sealed class StreamingRewriteSession<TState> : IDisposable
{
    private readonly IQueryExecution<TState> _execution;
    private readonly IUtf8HtmlTokenizerInput _input;
    private readonly Utf8StreamingRewriteCollector _collector;
    private bool _completed;
    private bool _disposed;

    internal StreamingRewriteSession(
        QueryPlan<TState> plan,
        TState state,
        IBufferWriter<byte> output,
        RewriteHandler<TState> handler,
        Utf8InputContract inputContract,
        HtmlStreamingLimits limits
    )
        : this(plan, state, new Utf8StreamingRewriteCollector(output, limits), handler, inputContract, limits) { }

    internal StreamingRewriteSession(
        QueryPlan<TState> plan,
        TState state,
        StreamingRewriteSegmentSink sink,
        RewriteHandler<TState> handler,
        Utf8InputContract inputContract,
        HtmlStreamingLimits limits
    )
        : this(plan, state, new Utf8StreamingRewriteCollector(sink, limits), handler, inputContract, limits) { }

    private StreamingRewriteSession(
        QueryPlan<TState> plan,
        TState state,
        Utf8StreamingRewriteCollector collector,
        RewriteHandler<TState> handler,
        Utf8InputContract inputContract,
        HtmlStreamingLimits limits
    )
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (inputContract is not (Utf8InputContract.ArbitraryBytes or Utf8InputContract.WellFormedUtf8))
            throw new ArgumentOutOfRangeException(nameof(inputContract));

        _collector = collector;
        try
        {
            if (limits.EnforcesLimits)
            {
                var execution = plan.CreateExecution<EnforcedResourceLimits>(state, limits, handler, _collector);
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
                var execution = plan.CreateExecution<UnboundedResources>(state, limits, handler, _collector);
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
        catch
        {
            _collector.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Gets the state object; the rewrite handler may have mutated it after any <see cref="Write"/>. The
    /// caller-owned state remains available after this session is disposed.
    /// </summary>
    public TState State => _execution.State;

    /// <summary>
    /// Consumes the next input chunk and publishes every byte that can no longer change. Chunk
    /// boundaries may split UTF-8 sequences and HTML constructs freely.
    /// </summary>
    public void Write(ReadOnlySpan<byte> utf8)
    {
        ThrowIfDisposed();
        // Publishing happens inside the write, where the tokenizer reports each consumed span to
        // the collector while it is still addressable.
        _input.Write(utf8);
    }

    /// <inheritdoc cref="Write(ReadOnlySpan{byte})"/>
    public void Write(ReadOnlyMemory<byte> utf8) => Write(utf8.Span);

    /// <inheritdoc cref="Write(ReadOnlySpan{byte})"/>
    public void Write(byte[] utf8)
    {
        ArgumentNullException.ThrowIfNull(utf8);
        Write(utf8.AsSpan());
    }

    /// <summary>Signals end of input, publishes the remaining tail, and returns the final state.</summary>
    public TState Complete()
    {
        ThrowIfDisposed();
        if (!_completed)
        {
            _input.Complete();
            _collector.Finish();
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
        _collector.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
