#pragma warning disable CS1591 // Experimental API surface; shape is intentionally unsettled.

using System.IO.Pipelines;

namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal class Utf8HtmlTokenizer<TResourceLimits> : Utf8HtmlTokenizerCore
    where TResourceLimits : struct, IResourceLimitPolicy
{
    public Utf8HtmlTokenizer(IUtf8HtmlTokenSink sink)
        : this(sink, null, HtmlStreamingLimits.Default, countInputBytes: true) { }

    public Utf8HtmlTokenizer(IUtf8HtmlTokenSink sink, HtmlStreamingLimits limits)
        : this(sink, null, limits, countInputBytes: true) { }

    public Utf8HtmlTokenizer(IUtf8HtmlTokenSink sink, Utf8HtmlTokenizerStateMetrics? stateMetrics)
        : this(sink, stateMetrics, HtmlStreamingLimits.Default, countInputBytes: true) { }

    public Utf8HtmlTokenizer(
        IUtf8HtmlTokenSink sink,
        Utf8HtmlTokenizerStateMetrics? stateMetrics,
        HtmlStreamingLimits limits,
        Boolean countInputBytes
    )
        : base(sink, stateMetrics, limits, countInputBytes, TResourceLimits.Enabled) { }
}

internal sealed class Utf8HtmlTokenizer : Utf8HtmlTokenizer<EnforcedResourceLimits>
{
    public static ValueTask<Utf8HtmlTokenizerCounters> TokenizeAsync(
        PipeReader reader,
        IUtf8HtmlTokenSink sink,
        CancellationToken cancellationToken = default,
        HtmlStreamingLimits? limits = null,
        Utf8InputContract inputContract = Utf8InputContract.ArbitraryBytes
    )
    {
        limits ??= HtmlStreamingLimits.Default;
        return Utf8HtmlTokenizerPipeline.TokenizeAsync(reader, sink, cancellationToken, limits, inputContract);
    }

    public Utf8HtmlTokenizer(IUtf8HtmlTokenSink sink)
        : base(sink) { }

    public Utf8HtmlTokenizer(IUtf8HtmlTokenSink sink, HtmlStreamingLimits limits)
        : base(sink, limits) { }

    public Utf8HtmlTokenizer(IUtf8HtmlTokenSink sink, Utf8HtmlTokenizerStateMetrics? stateMetrics)
        : base(sink, stateMetrics) { }

    public Utf8HtmlTokenizer(
        IUtf8HtmlTokenSink sink,
        Utf8HtmlTokenizerStateMetrics? stateMetrics,
        HtmlStreamingLimits limits,
        Boolean countInputBytes
    )
        : base(sink, stateMetrics, limits, countInputBytes) { }
}
