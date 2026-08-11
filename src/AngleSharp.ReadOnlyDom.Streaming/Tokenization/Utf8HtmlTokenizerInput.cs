#pragma warning disable CS1591 // Experimental implementation detail; shape is intentionally unsettled.

using System.IO.Pipelines;

namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal interface IUtf8HtmlTokenizerInput
{
    Utf8HtmlTokenizerCounters Counters { get; }

    void Write(ReadOnlyMemory<Byte> input);

    void Write(ReadOnlySpan<Byte> input);

    void Complete();
}

internal static class Utf8HtmlTokenizerPipeline
{
    internal static IUtf8HtmlTokenizer CreateTokenizer(
        IUtf8HtmlTokenSink sink,
        HtmlStreamingLimits limits,
        Boolean countInputBytes = true
    ) =>
        limits.EnforcesLimits
            ? new Utf8HtmlTokenizer(sink, stateMetrics: null, limits, countInputBytes)
            : new Utf8HtmlTokenizer<UnboundedResources>(sink, stateMetrics: null, limits, countInputBytes);

    internal static IUtf8HtmlTokenizerInput CreateInput(
        IUtf8HtmlTokenSink sink,
        Utf8InputContract inputContract,
        HtmlStreamingLimits limits
    ) =>
        limits.EnforcesLimits
            ? CreateInput<EnforcedResourceLimits>(sink, inputContract, limits)
            : CreateInput<UnboundedResources>(sink, inputContract, limits);

    private static Utf8HtmlTokenizerInput<TResourceLimits> CreateInput<TResourceLimits>(
        IUtf8HtmlTokenSink sink,
        Utf8InputContract inputContract,
        HtmlStreamingLimits limits
    )
        where TResourceLimits : struct, IResourceLimitPolicy
    {
        var tokenizer = new Utf8HtmlTokenizer<TResourceLimits>(sink, limits);
        return new Utf8HtmlTokenizerInput<TResourceLimits>(tokenizer, inputContract, limits);
    }

    internal static async ValueTask<Utf8HtmlTokenizerCounters> TokenizeAsync(
        PipeReader reader,
        IUtf8HtmlTokenSink sink,
        CancellationToken cancellationToken,
        HtmlStreamingLimits limits,
        Utf8InputContract inputContract
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (inputContract is not (Utf8InputContract.ArbitraryBytes or Utf8InputContract.WellFormedUtf8))
            throw new ArgumentOutOfRangeException(nameof(inputContract));

        var input = CreateInput(sink, inputContract, limits);
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
                    input.Write(segment);
                }
            }
            finally
            {
                reader.AdvanceTo(buffer.End);
            }
            if (result.IsCompleted)
            {
                break;
            }
        }
        input.Complete();
        return input.Counters;
    }
}

/// <summary>
/// Frames streaming input, enforces the source-byte limit, and optionally repairs malformed UTF-8
/// before complete, well-formed spans reach <see cref="Utf8HtmlTokenizer"/>.
/// </summary>
internal class Utf8HtmlTokenizerInput<TResourceLimits> : IUtf8HtmlTokenizerInput
    where TResourceLimits : struct, IResourceLimitPolicy
{
    private Utf8InputNormalizer<TResourceLimits> _normalizer;
    private readonly Utf8HtmlTokenizer<TResourceLimits> _tokenizer;
    private Boolean _completed;

    public Utf8HtmlTokenizerInput(
        Utf8HtmlTokenizer<TResourceLimits> tokenizer,
        Utf8InputContract inputContract = Utf8InputContract.ArbitraryBytes,
        HtmlStreamingLimits? limits = null
    )
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        limits ??= HtmlStreamingLimits.Default;
        _tokenizer = tokenizer;
        _normalizer = new Utf8InputNormalizer<TResourceLimits>(limits.MaximumInputBytes, inputContract);
    }

    public Utf8HtmlTokenizerCounters Counters => _tokenizer.GetCounters(_normalizer.BytesConsumed);

    public void Write(ReadOnlyMemory<Byte> input)
    {
        ThrowIfCompleted();
        _tokenizer.RecordInputSegment();
        _normalizer.Write(_tokenizer, input.Span, yieldOnRequest: false);
    }

    public void Write(ReadOnlySpan<Byte> input)
    {
        ThrowIfCompleted();
        _tokenizer.RecordInputSegment();
        _normalizer.Write(_tokenizer, input, yieldOnRequest: false);
    }

    public void Complete()
    {
        if (_completed)
            return;

        _normalizer.Complete(_tokenizer);
        _tokenizer.Complete();
        _completed = true;
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
            throw new InvalidOperationException("The UTF-8 tokenizer input has already completed.");
    }
}

internal sealed class Utf8HtmlTokenizerInput : Utf8HtmlTokenizerInput<EnforcedResourceLimits>
{
    public Utf8HtmlTokenizerInput(
        Utf8HtmlTokenizer tokenizer,
        Utf8InputContract inputContract = Utf8InputContract.ArbitraryBytes,
        HtmlStreamingLimits? limits = null
    )
        : base(tokenizer, inputContract, limits) { }
}
