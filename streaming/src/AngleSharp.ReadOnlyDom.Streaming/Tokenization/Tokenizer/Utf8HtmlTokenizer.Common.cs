using System.Buffers;
using System.Runtime.CompilerServices;

namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal partial class Utf8HtmlTokenizer<TResourceLimits> : IUtf8HtmlTokenizer
    where TResourceLimits : struct, IResourceLimitPolicy
{
    private readonly Utf8HtmlTokenizerStateMetrics? _stateMetrics;
    private readonly Utf8TokenBuffer _name = new(32);
    private readonly Utf8TokenBuffer _attributeName = new(32);
    private Utf8TokenBuffer? _attributeValue;
    private Utf8TokenBuffer? _seenAttributeNames;
    private readonly Utf8TokenBuffer _candidate = new(64);
    private Utf8TokenBuffer? _doctypePublic;
    private Utf8TokenBuffer? _doctypeSystem;
    private State _state;
    private State _returnState;
    private Boolean _isEndTag;
    private Boolean _startTagEmitted;
    private Boolean _captureStartTagAttributes;
    private UInt64 _attributeNameFilter;
    private Boolean _captureText;
    private Boolean _pendingCarriageReturn;
    private String? _rawEndTag;
    private Int64 _segments;
    private Int64 _reconsumes;
    private Int64 _bufferedTokenBytes;
    private Int32 _maximumBufferedTokenBytes;
    private Int32 _textUtf8CarryLength;
    private Int32 _textUtf8ExpectedLength;
    private UInt32 _textUtf8Carry;
    private Boolean _numericReferenceOverflow;
    private Boolean _numericReferenceHasDigits;
    private UInt32 _numericReferenceValue;
    private Boolean _yieldRequested;
    private Boolean _completed;
    private Utf8HtmlNameIdentityCache _tagNameIdentityCache;
    private Utf8HtmlNameIdentityCache _attributeNameIdentityCache;
    private Utf8CompactAttributeNameSet? _seenCompactAttributeNames;
    private Utf8AttributeNameIndex.Entry[]? _seenAttributeIndex;
    private Int32 _seenFallbackAttributeCount;
    private AttributeCapture _attributeCapture;
    private readonly Int32 _maximumBufferedTokenBytesAllowed;
    private readonly Int64 _maximumInputBytesAllowed;
    private readonly IUtf8HtmlTokenSink _sink;
    private readonly IUtf8HtmlStreamingCommentSink? _streamingCommentSink;
    private IUtf8HtmlStartTagSourceRangeSink? _startTagSourceRangeSink;
    private Byte _streamingFlags;
    private Int64 _normalizedBytesConsumed;
    private Int64 _inputBytesConsumed;
    private Int64 _currentSourceOffset;
    private Int64 _lastLessThanSourceOffset;
    private Int64 _currentTagSourceOffset;

    // Declared last on purpose. Inserting this beside _attributeNameFilter shifted the offsets of
    // every field declared after it and cost 1.1% retired instructions on news.google - a document
    // with 8 capturing tags, which cannot pay that from the pre-filter itself. Keeping new tokenizer
    // state at the end leaves the hot fields' offsets untouched.
    private UInt64 _attributeNameLengths;

    // Allowed sets for input that skipped UTF-8 validation contain the ASCII bytes that are NOT
    // terminators. The feature partials own the concrete sets; this shared helper builds them.
    private static SearchValues<Byte> CreateArbitraryAllowed(ReadOnlySpan<Byte> terminators)
    {
        Span<Byte> allowed = stackalloc Byte[128];
        var count = 0;
        for (var value = 0; value < 0x80; value++)
        {
            if (!terminators.Contains((Byte)value))
            {
                allowed[count++] = (Byte)value;
            }
        }

        return SearchValues.Create(allowed[..count]);
    }

    private static Int32 IndexOfCaptureStop<TTrust>(
        ReadOnlySpan<Byte> utf8,
        SearchValues<Byte> terminators,
        SearchValues<Byte> arbitraryAllowed
    )
        where TTrust : struct, IInputTrustPolicy =>
        TTrust.StopAtNonAscii ? utf8.IndexOfAnyExcept(arbitraryAllowed) : utf8.IndexOfAny(terminators);

    private Utf8TokenBuffer AttributeValue => _attributeValue ??= new(128);

    private Utf8TokenBuffer DoctypePublic => _doctypePublic ??= new(64);

    private Utf8TokenBuffer DoctypeSystem => _doctypeSystem ??= new(64);

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
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(sink);

        _sink = sink;
        RefreshCapture();
        _streamingCommentSink = sink as IUtf8HtmlStreamingCommentSink;
        if (sink is IUtf8HtmlRawTextSink { IsRawTextEnabled: true })
            _streamingFlags = RawTextEnabledFlag;
        RefreshStartTagSourceRangeSink();
        _stateMetrics = stateMetrics;
        _maximumBufferedTokenBytesAllowed = limits.MaximumBufferedTokenBytes;
        _maximumInputBytesAllowed = countInputBytes ? limits.MaximumInputBytes : Int64.MaxValue;
    }

    public static Int32 StateCount => StateNames.Length;

    public IReadOnlyList<Utf8HtmlTokenizerStateMetric> GetStateMetrics() => _stateMetrics?.Snapshot(StateNames) ?? [];

    // Per-run state accounting is diagnostics-only, but its probes are threaded through the two
    // bulk-scan methods, where they survived as real calls in the tier-1 body and inflated the
    // code the JIT had to allocate registers for. The scan loop is therefore instantiated over a
    // policy struct: TMetrics.Enabled is a compile-time constant per instantiation, so the
    // metrics-off body drops the probes entirely while the metrics-on body keeps recording.
    private interface IStateMetricsPolicy
    {
        static abstract Boolean Enabled { get; }
    }

    private readonly struct MetricsOff : IStateMetricsPolicy
    {
        public static Boolean Enabled => false;
    }

    private readonly struct MetricsOn : IStateMetricsPolicy
    {
        public static Boolean Enabled => true;
    }

    // Same struct-generic trick for the input contract. The trusted instantiation compiles to
    // exactly the previous body; the arbitrary-ASCII instantiation runs the identical state
    // machine over unvalidated input by stopping before any non-ASCII byte on the per-byte path
    // and adding every non-ASCII byte to the capturing bulk-scan stop sets, so a single scan both
    // finds the terminator and proves the skipped run valid. Non-ASCII runs bounce back to the
    // caller, which validates them and re-feeds them through the trusted entry point.
    private interface IInputTrustPolicy
    {
        static abstract Boolean StopAtNonAscii { get; }
    }

    private readonly struct TrustedInput : IInputTrustPolicy
    {
        public static Boolean StopAtNonAscii => false;
    }

    private readonly struct ArbitraryAsciiInput : IInputTrustPolicy
    {
        public static Boolean StopAtNonAscii => true;
    }

    // The threaded tag-tail scanner is shared by discarded and captured tags through the same
    // policy trick. CaptureOff folds every capture action away, compiling to the pure structural
    // scan discarded tails always had; CaptureOn inlines the attribute name/value bookkeeping the
    // per-byte machine would otherwise perform one Process call per delimiter byte.
    private interface IAttributeCapturePolicy
    {
        static abstract Boolean Enabled { get; }
    }

    private readonly struct CaptureOff : IAttributeCapturePolicy
    {
        public static Boolean Enabled => false;
    }

    private readonly struct CaptureOn : IAttributeCapturePolicy
    {
        public static Boolean Enabled => true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordState<TMetrics>(Int32 state, Int32 count)
        where TMetrics : struct, IStateMetricsPolicy
    {
        if (!TMetrics.Enabled)
        {
            return;
        }

        _stateMetrics!.Record(state, count);
    }

    // Cold paths keep the ordinary null check; only one probe each, so specialising them buys
    // nothing and would double the code for the per-byte state machine.
    private void RecordState(Int32 state, Int32 count) => _stateMetrics?.Record(state, count);

    // Metrics for the fused '<' + tag-open transfer, kept out of the hot scan arm; records the
    // same states the surrendered per-byte path would have walked.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RecordFusedTagOpen(Boolean isEndTag)
    {
        _stateMetrics!.Record((Int32)State.Data, 1);
        _stateMetrics.Record((Int32)State.TagOpen, 1);
        if (isEndTag)
        {
            _stateMetrics.Record((Int32)State.EndTagOpen, 1);
        }
    }

    // Metrics for the fused tag-name stop byte. Process() records one visit per dispatched byte, so
    // consuming the delimiter in the scan arm has to record the TagName visit it replaces or the
    // per-state counts silently lose one byte per tag.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RecordFusedTagNameStop() => _stateMetrics!.Record((Int32)State.TagName, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordFusedTagNameStopIf<TMetrics>()
        where TMetrics : struct, IStateMetricsPolicy
    {
        if (TMetrics.Enabled)
        {
            RecordFusedTagNameStop();
        }
    }

    public Utf8HtmlTokenizerCounters Counters => GetCounters(_inputBytesConsumed);

    internal Utf8HtmlTokenizerCounters GetCounters(Int64 sourceBytesConsumed) =>
        new(sourceBytesConsumed, _segments, _reconsumes, _maximumBufferedTokenBytes);

    /// <summary>
    /// Applies the tokenizer state selected by an external tree constructor.
    /// </summary>
    public void SetMode(HtmlParseMode mode, String? contextTagName)
    {
        _rawEndTag = mode switch
        {
            HtmlParseMode.RCData => "rcdata:" + (contextTagName ?? "\0"),
            HtmlParseMode.Rawtext => contextTagName ?? "\0",
            HtmlParseMode.Script => contextTagName ?? "script",
            HtmlParseMode.Plaintext => "\0",
            _ => null,
        };
        _state = mode switch
        {
            HtmlParseMode.RCData or HtmlParseMode.Rawtext => State.RawText,
            HtmlParseMode.Script => State.ScriptData,
            HtmlParseMode.Plaintext => State.Plaintext,
            _ => State.Data,
        };
    }

    public Boolean IsAcceptingCharacterData { get; set; }

    public Boolean IsModeControlledExternally { get; set; }

    public Boolean IsNotConsumingCharacterReferences { get; set; }

    public Boolean IsSupportingProcessingInstructions { get; set; }

    public Boolean SkipProcessingInstructions { get; set; }

    public Boolean SkipCDATA { get; set; }

    internal void RefreshStartTagSourceRangeSink() =>
        _startTagSourceRangeSink = _sink
            is IUtf8HtmlStartTagSourceRangeSink { WantsStartTagSourceRanges: true } sourceRangeSink
            ? sourceRangeSink
            : null;

    /// <summary>
    /// Enters the CDATA section state after the tree constructor accepts a CDATA declaration in foreign content.
    /// </summary>
    public void EnterCDataSection() => _state = State.CDataSection;
}
