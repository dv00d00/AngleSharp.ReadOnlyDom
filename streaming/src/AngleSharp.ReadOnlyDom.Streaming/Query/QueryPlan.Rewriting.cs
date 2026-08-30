using System.Buffers;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming.Query.Execution;
using AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

namespace AngleSharp.ReadOnlyDom.Streaming.Query;

public sealed partial class QueryPlan<TState>
{
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

    /// <summary>Rewrites matching elements and/or their raw text chunks.</summary>
    public TState Rewrite(
        ReadOnlySpan<byte> utf8,
        IBufferWriter<byte> output,
        TState state,
        HtmlRewriteHandlers<TState> handlers,
        Utf8InputContract inputContract = Utf8InputContract.ArbitraryBytes,
        HtmlStreamingLimits? limits = null
    )
    {
        ArgumentNullException.ThrowIfNull(output);
        using var execution = RewriteCore(utf8, state, handlers, inputContract, limits, out var collector);
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

    /// <summary>Rewrites with element and/or raw text handlers and publishes borrowed output segments.</summary>
    public TState Rewrite(
        ReadOnlySpan<byte> utf8,
        TState state,
        HtmlRewriteHandlers<TState> handlers,
        RewriteSegmentSink<TState> sink,
        Utf8InputContract inputContract = Utf8InputContract.ArbitraryBytes,
        HtmlStreamingLimits? limits = null
    )
    {
        ArgumentNullException.ThrowIfNull(sink);
        using var execution = RewriteCore(utf8, state, handlers, inputContract, limits, out var collector);
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
        return RewriteCore(
            utf8,
            state,
            new HtmlRewriteHandlers<TState>(element: handler),
            inputContract,
            limits,
            out collector
        );
    }

    private IQueryExecution<TState> RewriteCore(
        ReadOnlySpan<byte> utf8,
        TState state,
        HtmlRewriteHandlers<TState> handlers,
        Utf8InputContract inputContract,
        HtmlStreamingLimits? limits,
        out Utf8RewriteCollector collector
    )
    {
        if (handlers.IsEmpty)
            throw new ArgumentException("At least one rewrite handler is required.", nameof(handlers));
        if (inputContract == Utf8InputContract.ArbitraryBytes && !System.Text.Unicode.Utf8.IsValid(utf8))
            throw new DecoderFallbackException("Query rewriting requires well-formed UTF-8 input.");
        if (inputContract is not (Utf8InputContract.ArbitraryBytes or Utf8InputContract.WellFormedUtf8))
            throw new ArgumentOutOfRangeException(nameof(inputContract));

        limits ??= HtmlStreamingLimits.Default;
        collector = new Utf8RewriteCollector();
        IQueryExecution<TState> execution = limits.EnforcesLimits
            ? new QueryExecution<TState>(this, state, limits, handlers.Element, handlers.Text, collector)
            : new QueryExecution<TState, UnboundedResources>(
                this,
                state,
                limits,
                handlers.Element,
                handlers.Text,
                collector
            );
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
}
