#pragma warning disable CS1591 // Experimental API surface; shape is intentionally unsettled.

using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal interface IUtf8HtmlTokenizer
{
    Utf8HtmlTokenizerCounters Counters { get; }

    void Write(ReadOnlyMemory<Byte> utf8);

    void Write(ReadOnlySpan<Byte> utf8);

    void Complete();
}

internal class Utf8HtmlTokenizer<TResourceLimits> : IUtf8HtmlTokenizer
    where TResourceLimits : struct, IResourceLimitPolicy
{
    private const Int32 AttributeIndexPromotionThreshold = 16;
    private const UInt64 IframeKey = 0x000000001CBB9A4AUL;
    private const UInt64 NoEmbedKey = 0x00000004E8A91D49UL;
    private const UInt64 NoFramesKey = 0x0000009D17734958UL;
    private const UInt64 PlaintextKey = 0x000015899D3CABB9UL;
    private const UInt64 ScriptKey = 0x00000000308BBAB9UL;
    private const UInt64 StyleKey = 0x00000000018CFA2AUL;
    private const UInt64 TextareaKey = 0x000000CABB935D46UL;
    private const UInt64 TitleKey = 0x000000000197662AUL;
    private const UInt64 XmpKey = 0x0000000000007655UL;

    private enum State : byte
    {
        Data,
        Plaintext,
        TagOpen,
        EndTagOpen,
        TagName,
        BeforeAttributeName,
        AttributeName,
        AfterAttributeName,
        BeforeAttributeValue,
        AttributeValueDoubleQuoted,
        AttributeValueSingleQuoted,
        AttributeValueUnquoted,
        AfterAttributeValueQuoted,
        SelfClosingStartTag,
        MarkupDeclaration,
        CommentStart,
        CommentStartDash,
        Comment,
        CommentLessThan,
        CommentLessThanBang,
        CommentLessThanBangDash,
        CommentLessThanBangDashDash,
        CommentEndDash,
        CommentEnd,
        CommentEndBang,
        BogusComment,
        ProcessingInstruction,
        Doctype,
        CharacterReference,
        CDataSection,
        CDataSectionBracket,
        CDataSectionEnd,
        RawText,
        RawLessThan,
        RawEndTagOpen,
        RawEndTagName,
        ScriptData,
        ScriptLessThan,
        ScriptEndTagName,
        ScriptEscapeStart,
        ScriptEscapeStartDash,
        ScriptEscaped,
        ScriptEscapedDash,
        ScriptEscapedDashDash,
        ScriptEscapedLessThan,
        ScriptEscapedEndTagName,
        ScriptDoubleEscapeStart,
        ScriptDoubleEscaped,
        ScriptDoubleEscapedDash,
        ScriptDoubleEscapedDashDash,
        ScriptDoubleEscapedLessThan,
        ScriptDoubleEscapeEnd,
    }

    private enum DoctypeState : byte
    {
        BeforeName,
        Name,
        AfterName,
        AfterPublicKeyword,
        BeforePublicIdentifier,
        PublicIdentifierDoubleQuoted,
        PublicIdentifierSingleQuoted,
        AfterPublicIdentifier,
        BetweenPublicAndSystemIdentifiers,
        AfterSystemKeyword,
        BeforeSystemIdentifier,
        SystemIdentifierDoubleQuoted,
        SystemIdentifierSingleQuoted,
        AfterSystemIdentifier,
        Bogus,
    }

    private enum AttributeCapture : byte
    {
        Undecided,
        Capture,
        Discard,
        Duplicate,
    }

    private static readonly SearchValues<Byte> HtmlSpaces = SearchValues.Create("\t\n\f\r "u8);
    private static readonly SearchValues<Byte> DataTextTerminators = SearchValues.Create("<&\0\r"u8);
    private static readonly SearchValues<Byte> RawTextTerminators = SearchValues.Create("<\0\r"u8);
    private static readonly SearchValues<Byte> PlaintextTerminators = SearchValues.Create("\0\r"u8);
    private static readonly SearchValues<Byte> TagNameTerminators = SearchValues.Create("\0\t\n\f\r />"u8);
    private static readonly SearchValues<Byte> AttributeNameTerminators = SearchValues.Create("\0\t\n\f\r /=>"u8);
    private static readonly SearchValues<Byte> DiscardedAttributeNameTerminators = SearchValues.Create(
        "\t\n\f\r /=>"u8
    );
    // '&' never terminates a captured value scan: a character reference inside an attribute
    // value cannot affect tokenization, so references are decoded once over the contiguous
    // buffered value when the attribute commits instead of per byte during the scan.
    private static readonly SearchValues<Byte> DoubleQuotedAttributeValueTerminators = SearchValues.Create("\"\0\r"u8);
    private static readonly SearchValues<Byte> SingleQuotedAttributeValueTerminators = SearchValues.Create("'\0\r"u8);
    private static readonly SearchValues<Byte> UnquotedAttributeValueTerminators = SearchValues.Create(
        "\0>\t\n\f\r "u8
    );
    private static readonly SearchValues<Byte> DiscardedUnquotedAttributeValueTerminators = SearchValues.Create(
        ">\t\n\f\r "u8
    );
    private static readonly SearchValues<Byte> EscapedScriptTextTerminators = SearchValues.Create("<-\0\r"u8);
    private static readonly SearchValues<Byte> CommentTerminators = SearchValues.Create("<-\0\r"u8);

    // Allowed sets for input that skipped UTF-8 validation: the ASCII bytes that are NOT
    // terminators. Scanning with IndexOfAnyExcept stops at the usual terminators and at every
    // non-ASCII byte, so a single scan both finds the next terminator and proves the skipped run
    // is plain ASCII (trivially valid UTF-8). Expressed as a complement because an ASCII-only set
    // keeps the vectorized ASCII searcher; listing terminators plus 0x80-0xFF as stops selects the
    // generic any-byte searcher, which scans an order of magnitude slower. Only scans that capture
    // bytes need these; discarded runs may swallow arbitrary bytes because nothing observes them.
    private static readonly SearchValues<Byte> DataTextArbitraryAllowed = CreateArbitraryAllowed("<&\0\r"u8);
    private static readonly SearchValues<Byte> RawTextArbitraryAllowed = CreateArbitraryAllowed("<\0\r"u8);
    private static readonly SearchValues<Byte> PlaintextArbitraryAllowed = CreateArbitraryAllowed("\0\r"u8);
    private static readonly SearchValues<Byte> TagNameArbitraryAllowed = CreateArbitraryAllowed("\0\t\n\f\r />"u8);
    private static readonly SearchValues<Byte> AttributeNameArbitraryAllowed = CreateArbitraryAllowed(
        "\0\t\n\f\r /=>"u8
    );
    private static readonly SearchValues<Byte> DoubleQuotedAttributeValueArbitraryAllowed = CreateArbitraryAllowed(
        "\"\0\r"u8
    );
    private static readonly SearchValues<Byte> SingleQuotedAttributeValueArbitraryAllowed = CreateArbitraryAllowed(
        "'\0\r"u8
    );
    private static readonly SearchValues<Byte> UnquotedAttributeValueArbitraryAllowed = CreateArbitraryAllowed(
        "\0>\t\n\f\r "u8
    );
    private static readonly SearchValues<Byte> CommentArbitraryAllowed = CreateArbitraryAllowed("<-\0\r"u8);
    private static readonly SearchValues<Byte> EscapedScriptTextArbitraryAllowed = CreateArbitraryAllowed("<-\0\r"u8);
    private static readonly String[] StateNames = Enum.GetNames<State>();

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

    private static Int32 IndexOfDiscardedRawTextStop(ReadOnlySpan<Byte> utf8)
    {
        // Single-byte IndexOf keeps the memchr-speed kernel; the follower check resolves
        // lone '<' bytes locally instead of surfacing each one to the dispatch loop.
        var offset = 0;
        while (true)
        {
            var found = utf8[offset..].IndexOf((Byte)'<');
            if (found < 0)
            {
                return -1;
            }
            var position = offset + found;
            if (position + 1 == utf8.Length)
            {
                // A trailing '<' may complete "</" in the next chunk; the per-byte machine holds it.
                return position;
            }
            if (utf8[position + 1] == (Byte)'/')
            {
                return position;
            }
            offset = position + 1;
        }
    }

    private static Int32 IndexOfDiscardedScriptDataStop(ReadOnlySpan<Byte> utf8)
    {
        var offset = 0;
        while (true)
        {
            var found = utf8[offset..].IndexOf((Byte)'<');
            if (found < 0)
            {
                return -1;
            }
            var position = offset + found;
            if (position + 1 == utf8.Length)
            {
                return position;
            }
            var next = utf8[position + 1];
            if (next == (Byte)'/')
            {
                return position;
            }
            if (next != (Byte)'!')
            {
                offset = position + 1;
                continue;
            }
            // "<!" only matters when it completes "<!--"; a split candidate defers to the
            // per-byte machine, which can wait for the next chunk.
            if (position + 2 == utf8.Length)
            {
                return position;
            }
            if (utf8[position + 2] != (Byte)'-')
            {
                offset = position + 2;
                continue;
            }
            if (position + 3 == utf8.Length)
            {
                return position;
            }
            if (utf8[position + 3] == (Byte)'-')
            {
                return position;
            }
            offset = position + 3;
        }
    }

    private readonly Utf8HtmlTokenizerStateMetrics? _stateMetrics;
    private readonly Utf8TokenBuffer _name = new(32);
    private readonly Utf8TokenBuffer _attributeName = new(32);
    private Utf8TokenBuffer? _attributeValue;
    private Utf8TokenBuffer? _decodedAttributeValue;
    private Utf8TokenBuffer? _seenAttributeNames;
    private readonly Utf8TokenBuffer _candidate = new(64);
    private Utf8TokenBuffer? _doctypePublic;
    private Utf8TokenBuffer? _doctypeSystem;
    private State _state;
    private State _returnState;
    private Boolean _isEndTag;
    private Boolean _startTagEmitted;
    private Boolean _captureStartTagAttributes;
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
    private Boolean _streamingCommentStarted;
    private Boolean _captureStreamingComment;
    private Int64 _normalizedBytesConsumed;
    private Int64 _inputBytesConsumed;
    private Int64 _currentSourceOffset;
    private Int64 _lastLessThanSourceOffset;
    private Int64 _currentTagSourceOffset;

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

    /// <summary>
    /// Consumes complete, well-formed UTF-8. Use <see cref="Utf8HtmlTokenizerInput"/> for arbitrary
    /// byte chunks or malformed-input replacement.
    /// </summary>
    public void Write(ReadOnlyMemory<Byte> utf8)
    {
        RecordInputSegment();
        WriteCore(utf8.Span, yieldOnRequest: false);
    }

    /// <inheritdoc cref="Write(ReadOnlyMemory{Byte})"/>
    public void Write(ReadOnlySpan<Byte> utf8)
    {
        RecordInputSegment();
        WriteCore(utf8, yieldOnRequest: false);
    }

    /// <summary>
    /// Consumes input until the sink requests a yield. The caller must resubmit the unconsumed suffix before offering
    /// unrelated input.
    /// </summary>
    /// <returns>The number of bytes consumed from <paramref name="utf8"/>.</returns>
    internal Int32 WriteUntilYield(ReadOnlySpan<Byte> utf8)
    {
        ResetYieldRequest();
        return WriteCore(utf8, yieldOnRequest: true);
    }

    internal void RequestYield() => _yieldRequested = true;

    internal void ResetYieldRequest() => _yieldRequested = false;

    internal void RecordInputSegment() => _segments++;

    private Int32 WriteCore(ReadOnlySpan<Byte> utf8, Boolean yieldOnRequest)
    {
        ThrowIfCompleted();
        var previousBytesConsumed = 0L;
        if (TResourceLimits.Enabled)
        {
            previousBytesConsumed = _inputBytesConsumed;
            var observedInputBytes = SaturatingAdd(previousBytesConsumed, utf8.Length);
            if (observedInputBytes > _maximumInputBytesAllowed)
            {
                throw new HtmlStreamingLimitExceededException(
                    HtmlStreamingLimit.InputBytes,
                    _maximumInputBytesAllowed,
                    observedInputBytes
                );
            }
        }

        var consumed = WriteTrustedUtf8(utf8, yieldOnRequest);
        if (TResourceLimits.Enabled)
        {
            _inputBytesConsumed = SaturatingAdd(previousBytesConsumed, consumed);
        }
        return consumed;
    }

    internal Boolean IsYieldRequested => _yieldRequested;

    internal Int32 WriteTrustedUtf8(ReadOnlySpan<Byte> utf8, Boolean yieldOnRequest) =>
        _stateMetrics is null
            ? WriteUtf8<MetricsOff, TrustedInput>(utf8, yieldOnRequest)
            : WriteUtf8<MetricsOn, TrustedInput>(utf8, yieldOnRequest);

    internal Boolean TracksStartTagSourceRanges => _startTagSourceRangeSink is not null;

    /// <summary>
    /// The normalized-input offset before which no future start-tag edit can land. Only an
    /// unterminated start tag can still receive an insertion at its eventual close, so while one is
    /// open the offset pins to its '&lt;'; every insertion point and separator look-back of any
    /// future <see cref="IUtf8HtmlStartTagSourceRangeSink.StartTagSourceRange"/> sits at or above
    /// the returned value. Meaningful only while <see cref="TracksStartTagSourceRanges"/> is set,
    /// and only at quiescent points (between <see cref="Write"/> calls).
    /// </summary>
    internal Int64 RewritePublishableOffset
    {
        get
        {
            if (_completed)
            {
                return _normalizedBytesConsumed;
            }
            switch (_state)
            {
                case State.TagOpen:
                    // Could still become a start tag; its '<' is already consumed.
                    return _lastLessThanSourceOffset;
                case State.TagName:
                case State.BeforeAttributeName:
                case State.AttributeName:
                case State.AfterAttributeName:
                case State.BeforeAttributeValue:
                case State.AttributeValueDoubleQuoted:
                case State.AttributeValueSingleQuoted:
                case State.AttributeValueUnquoted:
                case State.AfterAttributeValueQuoted:
                case State.SelfClosingStartTag:
                    return _isEndTag ? _normalizedBytesConsumed : _currentTagSourceOffset;
                case State.CharacterReference:
                    // A reference inside a captured attribute value keeps the start tag open.
                    return !_isEndTag && IsTagTailState(_returnState)
                        ? _currentTagSourceOffset
                        : _normalizedBytesConsumed;
                default:
                    // End tags, comments, doctypes, raw text, and script data never produce edits.
                    return _normalizedBytesConsumed;
            }
        }
    }

    /// <summary>
    /// Consumes input that skipped UTF-8 validation, stopping before the first byte that would
    /// need it. Returns the number of bytes consumed; the byte at that position, if any, is
    /// non-ASCII and must be validated by the caller before re-entry via
    /// <see cref="WriteTrustedUtf8"/>. Must not be used while
    /// <see cref="TracksStartTagSourceRanges"/> is set: discarded text swallows unvalidated bytes
    /// raw, but an observing sink republishes the stream, which must be normalized UTF-8.
    /// </summary>
    internal Int32 WriteArbitraryAscii(ReadOnlySpan<Byte> utf8, Boolean yieldOnRequest) =>
        _stateMetrics is null
            ? WriteUtf8<MetricsOff, ArbitraryAsciiInput>(utf8, yieldOnRequest)
            : WriteUtf8<MetricsOn, ArbitraryAsciiInput>(utf8, yieldOnRequest);

    private Int32 WriteUtf8<TMetrics, TTrust>(ReadOnlySpan<Byte> utf8, Boolean yieldOnRequest)
        where TMetrics : struct, IStateMetricsPolicy
        where TTrust : struct, IInputTrustPolicy
    {
        var trackSourceRanges = _startTagSourceRangeSink is not null;
        var sourceBase = trackSourceRanges ? _normalizedBytesConsumed : 0;
        var index = 0;
        try
        {
            while (index < utf8.Length)
            {
                if (!_pendingCarriageReturn && (_isEndTag || _startTagEmitted) && IsTagTailState(_state))
                {
                    // A start tag is always emitted before its tail states, so the capture flag is
                    // settled here; end tags never capture, whatever the flag still says from the
                    // previous start tag (the raw-text end-tag path skips BeginTag).
                    var sourceOffset = trackSourceRanges ? sourceBase + index : 0;
                    var consumed = !_isEndTag && _captureStartTagAttributes
                        ? ScanTagTail<TMetrics, TTrust, CaptureOn>(
                            utf8[index..],
                            sourceOffset,
                            trackSourceRanges,
                            yieldOnRequest
                        )
                        : ScanTagTail<TMetrics, TTrust, CaptureOff>(
                            utf8[index..],
                            sourceOffset,
                            trackSourceRanges,
                            yieldOnRequest
                        );
                    if (consumed > 0)
                    {
                        index += consumed;
                        if (yieldOnRequest && _yieldRequested)
                        {
                            return index;
                        }
                        continue;
                    }
                }
                else if (_state == State.TagName)
                {
                    var remaining = utf8.Slice(index);
                    var run = IndexOfCaptureStop<TTrust>(remaining, TagNameTerminators, TagNameArbitraryAllowed);
                    run = run < 0 ? remaining.Length : run;

                    if (run > 0)
                    {
                        RecordState<TMetrics>((Int32)_state, run);
                        AppendTagName(remaining[..run]);
                        index += run;
                        continue;
                    }
                }
                else if (
                    _state is State.Data or State.RawText or State.ScriptData or State.Plaintext
                    && !_pendingCarriageReturn
                    && _textUtf8CarryLength == 0
                )
                {
                    var remaining = utf8.Slice(index);
                    Int32 run;
                    if (!_captureText)
                    {
                        // Discarded text may swallow arbitrary bytes raw - nothing observes it.
                        // Raw text and script data can also swallow lone '<' bytes: only the
                        // substrings "</" (an end-tag candidate) and, in script data, "<!--"
                        // (the escape start) can change the tokenizer state.
                        run = _state switch
                        {
                            State.Plaintext => remaining.Length,
                            State.ScriptData => IndexOfDiscardedScriptDataStop(remaining),
                            State.RawText => IndexOfDiscardedRawTextStop(remaining),
                            _ => remaining.IndexOf((Byte)'<'),
                        };
                    }
                    else
                    {
                        run = _state == State.Plaintext
                            ? IndexOfCaptureStop<TTrust>(remaining, PlaintextTerminators, PlaintextArbitraryAllowed)
                            : _state == State.Data || IsRcData()
                                ? IndexOfCaptureStop<TTrust>(remaining, DataTextTerminators, DataTextArbitraryAllowed)
                                : IndexOfCaptureStop<TTrust>(remaining, RawTextTerminators, RawTextArbitraryAllowed);
                    }
                    if (run < 0)
                    {
                        run = remaining.Length;
                    }
                    if (run > 0)
                    {
                        RecordState<TMetrics>((Int32)_state, run);
                        if (_captureText)
                        {
                            EmitText(utf8.Slice(index, run));
                        }
                        index += run;
                        continue;
                    }
                    // Stop-byte fusion: a '<' followed by an ASCII letter (or "/" + letter) in
                    // data state always begins a tag, so consume through the first name byte here
                    // instead of surrendering '<', the follower, and the letter to three per-byte
                    // dispatcher round-trips. Only data state qualifies: raw text, RCDATA, and
                    // script data route '<' through the end-tag candidate machinery below. All
                    // fused bytes are ASCII by test, so the trust policy is satisfied.
                    if (_state == State.Data && remaining[0] == (Byte)'<' && remaining.Length >= 2)
                    {
                        Int32 fused;
                        Boolean isEndTag;
                        if (IsAsciiLetter(remaining[1]))
                        {
                            fused = 2;
                            isEndTag = false;
                        }
                        else if (
                            remaining[1] == (Byte)'/'
                            && remaining.Length >= 3
                            && IsAsciiLetter(remaining[2])
                        )
                        {
                            fused = 3;
                            isEndTag = true;
                        }
                        else
                        {
                            goto PerByte;
                        }
                        if (TMetrics.Enabled)
                        {
                            RecordFusedTagOpen(isEndTag);
                        }
                        index += fused;
                        if (trackSourceRanges)
                        {
                            _currentSourceOffset = sourceBase + index;
                            _lastLessThanSourceOffset = sourceBase + index - fused;
                        }
                        BeginTag(isEndTag, remaining[fused - 1]);
                        continue;
                    }
                    // A lone '<' in raw text or script data only opens a tag when '/' (or '!'
                    // in script data) follows; emitting it here keeps the scan in the bulk loop
                    // instead of bouncing through the per-byte candidate machinery.
                    if (
                        _state is State.RawText or State.ScriptData
                        && remaining.Length >= 2
                        && remaining[0] == (Byte)'<'
                        && remaining[1] != (Byte)'/'
                        && (_state == State.RawText || remaining[1] != (Byte)'!')
                    )
                    {
                        RecordState<TMetrics>((Int32)_state, 1);
                        EmitText("<"u8);
                        index++;
                        continue;
                    }
                }
                else if (_state == State.Comment && !_pendingCarriageReturn)
                {
                    var remaining = utf8.Slice(index);
                    var run = IndexOfCaptureStop<TTrust>(remaining, CommentTerminators, CommentArbitraryAllowed);
                    run = run < 0 ? remaining.Length : run;
                    if (run > 0)
                    {
                        RecordState<TMetrics>((Int32)_state, run);
                        AppendComment(remaining.Slice(0, run));
                        index += run;
                        continue;
                    }
                }
                PerByte:
                var value = utf8[index];
                if (TTrust.StopAtNonAscii && value >= 0x80)
                {
                    // Unvalidated non-ASCII: hand back to the caller, which validates the run and
                    // re-feeds it through the trusted entry point.
                    return index;
                }
                index++;
                if (trackSourceRanges)
                {
                    _currentSourceOffset = sourceBase + index;
                    if (value == (Byte)'<')
                    {
                        _lastLessThanSourceOffset = _currentSourceOffset - 1;
                    }
                }
                if (_pendingCarriageReturn)
                {
                    _pendingCarriageReturn = false;
                    if (value == (Byte)'\n')
                    {
                        continue;
                    }
                }
                if (value == (Byte)'\r')
                {
                    _pendingCarriageReturn = true;
                    value = (Byte)'\n';
                }
                if (IsScriptState(_state))
                {
                    ProcessScriptInput<TTrust>(value, utf8, ref index);
                }
                else
                {
                    Process(value);
                }
                if (yieldOnRequest && _yieldRequested)
                {
                    return index;
                }
            }

            return index;
        }
        finally
        {
            if (trackSourceRanges)
            {
                _normalizedBytesConsumed = sourceBase + index;
                // Only the consumed slice is reported, so partial consumption (yield, or the fused
                // ASCII path handing back at a non-ASCII byte) never double-observes the tail.
                _startTagSourceRangeSink!.ObserveNormalizedUtf8End(
                    sourceBase,
                    utf8[..index],
                    RewritePublishableOffset
                );
            }
        }
    }

    public void Complete()
    {
        if (_completed)
        {
            return;
        }

        Utf8AttributeNameIndex.Reset(ref _seenAttributeIndex);
        switch (_state)
        {
            case State.TagOpen:
                EmitText("<"u8);
                break;
            case State.EndTagOpen:
                EmitText("</"u8);
                break;
            case State.CharacterReference:
                ResolveCharacterReference();
                break;
            case State.CDataSectionBracket:
                EmitCDataText("]"u8);
                break;
            case State.CDataSectionEnd:
                EmitCDataText("]]"u8);
                break;
            case State.RawLessThan:
            case State.RawEndTagOpen:
            case State.RawEndTagName:
                EmitText(_candidate.WrittenSpan);
                break;
            case State.CommentStart:
            case State.CommentStartDash:
            case State.Comment:
            case State.CommentLessThan:
            case State.CommentLessThanBang:
            case State.CommentLessThanBangDash:
            case State.CommentLessThanBangDashDash:
            case State.CommentEndDash:
            case State.CommentEnd:
            case State.CommentEndBang:
            case State.BogusComment:
            case State.MarkupDeclaration:
                EmitComment();
                break;
            case State.ProcessingInstruction:
                EmitProcessingInstruction();
                break;
            case State.Doctype:
                EmitDoctype(forceEofQuirks: true);
                break;
            // EOF in a tag discards the incomplete token.
            case State.TagName:
            case State.BeforeAttributeName:
            case State.AttributeName:
            case State.AfterAttributeName:
            case State.BeforeAttributeValue:
            case State.AttributeValueDoubleQuoted:
            case State.AttributeValueSingleQuoted:
            case State.AttributeValueUnquoted:
            case State.AfterAttributeValueQuoted:
            case State.SelfClosingStartTag:
                break;
        }

        _sink.EndOfFile();
        _completed = true;
    }

    private void Process(Byte value)
    {
        var reconsume = true;
        while (reconsume)
        {
            reconsume = false;
            RecordState((Int32)_state, 1);
            switch (_state)
            {
                case State.Data:
                    if (value == (Byte)'<')
                    {
                        _state = State.TagOpen;
                    }
                    else if (value == (Byte)'&' && _captureText && !IsNotConsumingCharacterReferences)
                    {
                        BeginCharacterReference(State.Data);
                    }
                    else if (_captureText)
                    {
                        EmitByte(value);
                    }

                    break;
                case State.Plaintext:
                    if (value == 0)
                    {
                        EmitReplacementCharacter();
                    }
                    else
                    {
                        EmitByte(value);
                    }

                    break;
                case State.TagOpen:
                    if (value == (Byte)'/')
                    {
                        _state = State.EndTagOpen;
                    }
                    else if (value == (Byte)'!')
                    {
                        Clear(_candidate);
                        _state = State.MarkupDeclaration;
                    }
                    else if (value == (Byte)'?' && IsSupportingProcessingInstructions)
                    {
                        Clear(_candidate);
                        if (!SkipProcessingInstructions)
                        {
                            Append(_candidate, value);
                        }
                        _state = State.ProcessingInstruction;
                    }
                    else if (value == (Byte)'?')
                    {
                        Clear(_candidate);
                        Append(_candidate, value);
                        _state = State.BogusComment;
                    }
                    else if (IsAsciiLetter(value))
                    {
                        BeginTag(isEndTag: false, value);
                    }
                    else
                    {
                        EmitText("<"u8);
                        Reconsume(ref reconsume, State.Data);
                    }
                    break;
                case State.EndTagOpen:
                    if (IsAsciiLetter(value))
                    {
                        BeginTag(isEndTag: true, value);
                    }
                    else if (value == (Byte)'>')
                    {
                        _state = State.Data;
                    }
                    else
                    {
                        Clear(_candidate);
                        Reconsume(ref reconsume, State.BogusComment);
                    }
                    break;
                case State.TagName:
                    if (IsSpace(value))
                    {
                        EmitTagStart();
                        _state = State.BeforeAttributeName;
                    }
                    else if (value == (Byte)'/')
                    {
                        EmitTagStart();
                        _state = State.SelfClosingStartTag;
                    }
                    else if (value == (Byte)'>')
                    {
                        FinishTag(selfClosing: false);
                    }
                    else
                    {
                        AppendTagNameReplacedNull(value);
                    }

                    break;
                case State.BeforeAttributeName:
                    if (IsSpace(value))
                    {
                        break;
                    }

                    if (value == (Byte)'/')
                    {
                        _state = State.SelfClosingStartTag;
                        break;
                    }
                    if (value == (Byte)'>')
                    {
                        FinishTag(selfClosing: false);
                        break;
                    }
                    if (_captureStartTagAttributes)
                    {
                        Clear(_attributeName);
                        Clear(_attributeValue);
                        _attributeNameIdentityCache.Reset();
                        AppendReplacedNull(_attributeName, value, lowerAscii: false);
                    }
                    else
                    {
                        _attributeCapture = AttributeCapture.Discard;
                    }
                    _state = State.AttributeName;
                    break;
                case State.AttributeName:
                    if (IsSpace(value))
                    {
                        _state = State.AfterAttributeName;
                    }
                    else if (value == (Byte)'=')
                    {
                        DecideAttributeCapture();
                        _state = State.BeforeAttributeValue;
                    }
                    else if (value is (Byte)'/' or (Byte)'>')
                    {
                        CommitAttribute();
                        Reconsume(ref reconsume, State.BeforeAttributeName);
                    }
                    else
                    {
                        if (_captureStartTagAttributes)
                        {
                            AppendReplacedNull(_attributeName, value, lowerAscii: false);
                        }
                    }

                    break;
                case State.AfterAttributeName:
                    if (IsSpace(value))
                    {
                        break;
                    }

                    if (value == (Byte)'=')
                    {
                        DecideAttributeCapture();
                        _state = State.BeforeAttributeValue;
                        break;
                    }
                    CommitAttribute();
                    Reconsume(ref reconsume, State.BeforeAttributeName);
                    break;
                case State.BeforeAttributeValue:
                    if (IsSpace(value))
                    {
                        break;
                    }

                    if (value == (Byte)'"')
                    {
                        _state = State.AttributeValueDoubleQuoted;
                    }
                    else if (value == (Byte)'\'')
                    {
                        _state = State.AttributeValueSingleQuoted;
                    }
                    else if (value == (Byte)'>')
                    {
                        CommitAttribute();
                        FinishTag(selfClosing: false);
                    }
                    else
                    {
                        _state = State.AttributeValueUnquoted;
                        Reconsume(ref reconsume, _state);
                    }
                    break;
                case State.AttributeValueDoubleQuoted:
                case State.AttributeValueSingleQuoted:
                    var quote = _state == State.AttributeValueDoubleQuoted ? (Byte)'"' : (Byte)'\'';
                    if (value == quote)
                    {
                        _state = State.AfterAttributeValueQuoted;
                    }
                    else
                    {
                        // '&' is appended raw here: attribute character references are
                        // decoded over the buffered value when the attribute commits.
                        if (_attributeCapture == AttributeCapture.Capture)
                        {
                            AppendReplacedNull(AttributeValue, value, lowerAscii: false);
                        }
                    }
                    break;
                case State.AttributeValueUnquoted:
                    if (IsSpace(value))
                    {
                        CommitAttribute();
                        _state = State.BeforeAttributeName;
                    }
                    else if (value == (Byte)'>')
                    {
                        CommitAttribute();
                        FinishTag(selfClosing: false);
                    }
                    else
                    {
                        if (_attributeCapture == AttributeCapture.Capture)
                        {
                            AppendReplacedNull(AttributeValue, value, lowerAscii: false);
                        }
                    }
                    break;
                case State.AfterAttributeValueQuoted:
                    CommitAttribute();
                    if (IsSpace(value))
                    {
                        _state = State.BeforeAttributeName;
                    }
                    else if (value == (Byte)'/')
                    {
                        _state = State.SelfClosingStartTag;
                    }
                    else if (value == (Byte)'>')
                    {
                        FinishTag(selfClosing: false);
                    }
                    else
                    {
                        Reconsume(ref reconsume, State.BeforeAttributeName);
                    }

                    break;
                case State.SelfClosingStartTag:
                    if (value == (Byte)'>')
                    {
                        FinishTag(selfClosing: true);
                    }
                    else
                    {
                        Reconsume(ref reconsume, State.BeforeAttributeName);
                    }

                    break;
                case State.MarkupDeclaration:
                    ProcessMarkupDeclaration(value);
                    break;
                case State.CommentStart:
                    if (value == (Byte)'-')
                    {
                        _state = State.CommentStartDash;
                    }
                    else if (value == (Byte)'>')
                    {
                        EmitComment();
                    }
                    else
                    {
                        Reconsume(ref reconsume, State.Comment);
                    }

                    break;
                case State.CommentStartDash:
                    if (value == (Byte)'-')
                    {
                        _state = State.CommentEnd;
                    }
                    else if (value == (Byte)'>')
                    {
                        EmitComment();
                    }
                    else
                    {
                        AppendComment((Byte)'-');
                        Reconsume(ref reconsume, State.Comment);
                    }
                    break;
                case State.Comment:
                    if (value == (Byte)'<')
                    {
                        AppendComment(value);
                        _state = State.CommentLessThan;
                    }
                    else if (value == (Byte)'-')
                    {
                        _state = State.CommentEndDash;
                    }
                    else if (value == 0)
                    {
                        AppendCommentReplacement();
                    }
                    else
                    {
                        AppendComment(value);
                    }

                    break;
                case State.CommentLessThan:
                    if (value == (Byte)'!')
                    {
                        AppendComment(value);
                        _state = State.CommentLessThanBang;
                    }
                    else if (value == (Byte)'<')
                    {
                        AppendComment(value);
                    }
                    else
                    {
                        Reconsume(ref reconsume, State.Comment);
                    }

                    break;
                case State.CommentLessThanBang:
                    if (value == (Byte)'-')
                    {
                        _state = State.CommentLessThanBangDash;
                    }
                    else
                    {
                        Reconsume(ref reconsume, State.Comment);
                    }

                    break;
                case State.CommentLessThanBangDash:
                    if (value == (Byte)'-')
                    {
                        _state = State.CommentLessThanBangDashDash;
                    }
                    else
                    {
                        Reconsume(ref reconsume, State.CommentEndDash);
                    }

                    break;
                case State.CommentLessThanBangDashDash:
                    Reconsume(ref reconsume, State.CommentEnd);
                    break;
                case State.CommentEndDash:
                    if (value == (Byte)'-')
                    {
                        _state = State.CommentEnd;
                    }
                    else
                    {
                        AppendComment((Byte)'-');
                        Reconsume(ref reconsume, State.Comment);
                    }
                    break;
                case State.CommentEnd:
                    if (value == (Byte)'>')
                    {
                        EmitComment();
                    }
                    else if (value == (Byte)'!')
                    {
                        _state = State.CommentEndBang;
                    }
                    else if (value == (Byte)'-')
                    {
                        AppendComment(value);
                    }
                    else
                    {
                        AppendComment("--"u8);
                        Reconsume(ref reconsume, State.Comment);
                    }
                    break;
                case State.CommentEndBang:
                    if (value == (Byte)'>')
                    {
                        EmitComment();
                    }
                    else
                    {
                        AppendComment("--!"u8);
                        if (value == (Byte)'-')
                        {
                            _state = State.CommentEndDash;
                        }
                        else
                        {
                            Reconsume(ref reconsume, State.Comment);
                        }
                    }
                    break;
                case State.BogusComment:
                    if (value == (Byte)'>')
                    {
                        EmitComment();
                    }
                    else
                    {
                        AppendCommentReplacedNull(value);
                    }

                    break;
                case State.ProcessingInstruction:
                    if (value == (Byte)'>')
                    {
                        EmitProcessingInstruction();
                    }
                    else if (!SkipProcessingInstructions)
                    {
                        AppendReplacedNull(_candidate, value, lowerAscii: false);
                    }
                    break;
                case State.Doctype:
                    if (value == (Byte)'>')
                    {
                        EmitDoctype(forceEofQuirks: false);
                        _state = State.Data;
                    }
                    else
                    {
                        Append(_candidate, value);
                    }

                    break;
                case State.CharacterReference:
                    ProcessCharacterReference(value, ref reconsume);
                    break;
                case State.CDataSection:
                    if (value == (Byte)']')
                    {
                        _state = State.CDataSectionBracket;
                    }
                    else
                    {
                        EmitCDataByte(value);
                    }

                    break;
                case State.CDataSectionBracket:
                    if (value == (Byte)']')
                    {
                        _state = State.CDataSectionEnd;
                    }
                    else
                    {
                        EmitCDataText("]"u8);
                        Reconsume(ref reconsume, State.CDataSection);
                    }
                    break;
                case State.CDataSectionEnd:
                    if (value == (Byte)']')
                    {
                        EmitCDataText("]"u8);
                    }
                    else if (value == (Byte)'>')
                    {
                        _state = State.Data;
                    }
                    else
                    {
                        EmitCDataText("]]"u8);
                        Reconsume(ref reconsume, State.CDataSection);
                    }
                    break;
                case State.RawText:
                    if (value == (Byte)'<')
                    {
                        Clear(_candidate);
                        Append(_candidate, value);
                        _state = State.RawLessThan;
                    }
                    else if (value == (Byte)'&' && _captureText && IsRcData() && !IsNotConsumingCharacterReferences)
                    {
                        BeginCharacterReference(State.RawText);
                    }
                    else if (value == 0)
                    {
                        EmitReplacementCharacter();
                    }
                    else
                    {
                        EmitByte(value);
                    }

                    break;
                case State.RawLessThan:
                    if (value == (Byte)'/')
                    {
                        Append(_candidate, value);
                        _state = State.RawEndTagOpen;
                    }
                    else
                    {
                        EmitText(_candidate.WrittenSpan);
                        Clear(_candidate);
                        Reconsume(ref reconsume, State.RawText);
                    }
                    break;
                case State.RawEndTagOpen:
                case State.RawEndTagName:
                    if (IsAsciiLetter(value))
                    {
                        Append(_candidate, value);
                        _state = State.RawEndTagName;
                    }
                    else if (_state == State.RawEndTagName && IsTagDelimiter(value) && RawCandidateMatches())
                    {
                        Clear(_name);
                        _tagNameIdentityCache.Reset();
                        AppendTagName(_candidate.WrittenSpan.Slice(2));
                        Clear(_candidate);
                        _isEndTag = true;
                        _rawEndTag = null;
                        if (value == (Byte)'>')
                        {
                            FinishTag(selfClosing: false);
                        }
                        else if (value == (Byte)'/')
                        {
                            _state = State.SelfClosingStartTag;
                        }
                        else
                        {
                            _state = State.BeforeAttributeName;
                        }
                    }
                    else
                    {
                        EmitText(_candidate.WrittenSpan);
                        Clear(_candidate);
                        Reconsume(ref reconsume, State.RawText);
                    }
                    break;
            }
        }
    }

    private void ProcessMarkupDeclaration(Byte value)
    {
        if (value == (Byte)'>')
        {
            EmitComment();
            return;
        }

        AppendReplacedNull(_candidate, value, lowerAscii: false);
        var candidate = _candidate.WrittenSpan;
        if ("--"u8.StartsWith(candidate))
        {
            if (candidate.Length == 2)
            {
                Clear(_candidate);
                _state = State.CommentStart;
            }
            return;
        }
        if (StartsWithAsciiIgnoreCase("doctype"u8, candidate))
        {
            if (candidate.Length == 7)
            {
                Clear(_candidate);
                _state = State.Doctype;
            }
            return;
        }
        if (IsAcceptingCharacterData && "[CDATA["u8.StartsWith(candidate))
        {
            if (candidate.Length == 7)
            {
                Clear(_candidate);
                _state = State.CDataSection;
            }
            return;
        }
        _state = State.BogusComment;
    }

    private void EmitComment()
    {
        if (_streamingCommentSink is null)
        {
            _sink.Comment(_candidate.WrittenSpan);
        }
        else
        {
            EnsureStreamingCommentStarted();
            _streamingCommentSink.EndComment();
            _streamingCommentStarted = false;
            _captureStreamingComment = false;
        }
        Clear(_candidate);
        _state = State.Data;
    }

    private void EmitProcessingInstruction()
    {
        _sink.ProcessingInstruction(_candidate.WrittenSpan);
        Clear(_candidate);
        _state = State.Data;
    }

    private void AppendComment(Byte value)
    {
        Span<Byte> bytes = stackalloc Byte[1];
        bytes[0] = value;
        AppendComment(bytes);
    }

    private void AppendComment(ReadOnlySpan<Byte> value)
    {
        if (_streamingCommentSink is null)
        {
            Append(_candidate, value);
            return;
        }

        EnsureStreamingCommentStarted();
        if (_captureStreamingComment)
        {
            _streamingCommentSink.CommentChunk(value);
        }
    }

    private void AppendCommentReplacement() => AppendComment("\uFFFD"u8);

    private void AppendCommentReplacedNull(Byte value) =>
        AppendComment(value == 0 ? "\uFFFD"u8 : new ReadOnlySpan<Byte>(in value));

    private void EnsureStreamingCommentStarted()
    {
        if (_streamingCommentStarted)
        {
            return;
        }

        _captureStreamingComment = _streamingCommentSink!.BeginComment();
        _streamingCommentStarted = true;
        if (_captureStreamingComment && _candidate.WrittenCount != 0)
        {
            _streamingCommentSink.CommentChunk(_candidate.WrittenSpan);
        }
        Clear(_candidate);
    }

    private void ProcessCharacterReference(Byte value, ref Boolean reconsume)
    {
        var source = _candidate.WrittenSpan;
        if (!source.IsEmpty && source[0] == (Byte)'#')
        {
            if (source.Length == 1 && value is (Byte)'x' or (Byte)'X')
            {
                Append(_candidate, value);
                return;
            }

            if (value == (Byte)';')
            {
                if (!_numericReferenceOverflow)
                {
                    Append(_candidate, value);
                }

                ResolveCharacterReference();
                _state = _returnState;
                return;
            }

            var isHex = source.Length > 1 && source[1] is (Byte)'x' or (Byte)'X';
            var isDigit = isHex
                ? (UInt32)(value - '0') <= 9 || (UInt32)(AsciiLower(value) - 'a') <= 5
                : (UInt32)(value - '0') <= 9;
            if (isDigit)
            {
                var digit = (UInt32)(value - '0') <= 9 ? (UInt32)(value - '0') : (UInt32)(AsciiLower(value) - 'a' + 10);
                var radix = isHex ? 16u : 10u;
                _numericReferenceHasDigits = true;
                if (!_numericReferenceOverflow && _numericReferenceValue <= (0x10FFFFu - digit) / radix)
                {
                    _numericReferenceValue = _numericReferenceValue * radix + digit;
                }
                else
                {
                    _numericReferenceOverflow = true;
                }

                if (_candidate.WrittenCount < 32)
                {
                    Append(_candidate, value);
                }

                return;
            }

            ResolveCharacterReference(value);
            Reconsume(ref reconsume, _returnState);
            return;
        }

        if (value == (Byte)';')
        {
            Append(_candidate, value);
            ResolveCharacterReference();
            _state = _returnState;
            return;
        }
        var length = _candidate.WrittenCount;
        if (
            length < 32
            && (
                IsAsciiAlphaNumeric(value)
                || (length == 0 && value == (Byte)'#')
                || (length == 1 && _candidate.WrittenSpan[0] == (Byte)'#' && value is (Byte)'x' or (Byte)'X')
            )
        )
        {
            Append(_candidate, value);
            return;
        }
        ResolveCharacterReference(value);
        Reconsume(ref reconsume, _returnState);
    }

    private void ResolveCharacterReference(Byte? nextInput = null)
    {
        var source = _candidate.WrittenSpan;
        Span<Byte> replacement = stackalloc Byte[8];
        var replacementLength = 0;
        if (!source.IsEmpty && source[0] == (Byte)'#' && _numericReferenceHasDigits)
        {
            var scalar = (Int32)_numericReferenceValue;
            if (_numericReferenceOverflow)
            {
                replacementLength = WriteReplacementCharacter(replacement);
            }
            else
            {
                var mappedScalar = Utf8HtmlEntityDecoder.GetSymbolCodeFromTable(scalar);
                scalar = mappedScalar < 0 ? scalar : mappedScalar;
                replacementLength = Utf8HtmlEntityDecoder.IsInvalidNumber(scalar)
                    ? WriteReplacementCharacter(replacement)
                    : WriteScalarUtf8(scalar, replacement);
            }
        }
        else if (!source.IsEmpty)
        {
            var entityLength = Utf8HtmlEntityDecoder.WriteLongestSymbolUtf8(source, replacement, out var matchedLength);
            if (entityLength != 0)
            {
                var missingSemicolon = source[matchedLength - 1] != (Byte)';';
                if (
                    missingSemicolon
                    && IsAttributeReturnState()
                    && (
                        (
                            matchedLength < source.Length
                            && (source[matchedLength] == '=' || IsAsciiAlphaNumeric(source[matchedLength]))
                        )
                        || (
                            matchedLength == source.Length
                            && nextInput is Byte next
                            && (next == '=' || IsAsciiAlphaNumeric(next))
                        )
                    )
                )
                {
                    entityLength = 0;
                }
            }
            if (entityLength != 0)
            {
                AppendCharacterReferenceResult(replacement[..entityLength]);
                AppendCharacterReferenceResult(source[matchedLength..]);
                Clear(_candidate);
                return;
            }
        }

        if (replacementLength != 0)
        {
            AppendCharacterReferenceResult(replacement.Slice(0, replacementLength));
        }
        else
        {
            AppendCharacterReferenceResult("&"u8);
            AppendCharacterReferenceResult(source);
        }
        Clear(_candidate);
    }

    private static Int32 WriteReplacementCharacter(Span<Byte> destination)
    {
        "\uFFFD"u8.CopyTo(destination);
        return 3;
    }

    private static Int32 WriteScalarUtf8(Int32 scalar, Span<Byte> destination)
    {
        if (scalar <= 0x7F)
        {
            destination[0] = (Byte)scalar;
            return 1;
        }
        if (scalar <= 0x7FF)
        {
            destination[0] = (Byte)(0xC0 | (scalar >> 6));
            destination[1] = (Byte)(0x80 | (scalar & 0x3F));
            return 2;
        }
        if (scalar <= 0xFFFF)
        {
            destination[0] = (Byte)(0xE0 | (scalar >> 12));
            destination[1] = (Byte)(0x80 | ((scalar >> 6) & 0x3F));
            destination[2] = (Byte)(0x80 | (scalar & 0x3F));
            return 3;
        }

        destination[0] = (Byte)(0xF0 | (scalar >> 18));
        destination[1] = (Byte)(0x80 | ((scalar >> 12) & 0x3F));
        destination[2] = (Byte)(0x80 | ((scalar >> 6) & 0x3F));
        destination[3] = (Byte)(0x80 | (scalar & 0x3F));
        return 4;
    }

    private void AppendCharacterReferenceResult(ReadOnlySpan<Byte> utf8)
    {
        if (_returnState is State.Data or State.RawText)
        {
            EmitText(utf8);
        }
        else if (_attributeCapture == AttributeCapture.Capture)
        {
            Append(AttributeValue, utf8);
        }
    }

    private void BeginCharacterReference(State returnState)
    {
        Clear(_candidate);
        _numericReferenceOverflow = false;
        _numericReferenceHasDigits = false;
        _numericReferenceValue = 0;
        _returnState = returnState;
        _state = State.CharacterReference;
    }

    /// <summary>
    /// Decodes character references over a fully buffered attribute value. References in
    /// attribute values cannot affect tokenization, so the scan buffers their bytes raw and
    /// this pass resolves them once per attribute instead of routing every byte through the
    /// per-byte reference machinery. Mirrors the reference states in attribute context,
    /// including the missing-semicolon suppression rule; the value's real terminator (quote,
    /// space, or '&gt;') is never alphanumeric or '=', so the end of the buffer never
    /// suppresses a match.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DecodeAttributeValueReferences(
        ReadOnlySpan<Byte> value,
        Int32 ampersand,
        Utf8TokenBuffer destination
    )
    {
        var index = ampersand;
        Append(destination, value[..index]);
        while (true)
        {
            index = DecodeAttributeReference(value, index, destination);
            var run = value[index..].IndexOf((Byte)'&');
            if (run < 0)
            {
                Append(destination, value[index..]);
                return;
            }
            Append(destination, value.Slice(index, run));
            index += run;
        }
    }

    /// <summary>
    /// Decodes the single reference candidate whose '&amp;' sits at <paramref name="index"/>,
    /// appends its result to <paramref name="destination"/>, and returns the index of the
    /// first byte it did not consume.
    /// </summary>
    private Int32 DecodeAttributeReference(ReadOnlySpan<Byte> value, Int32 index, Utf8TokenBuffer destination)
    {
        var position = index + 1;
        if (position < value.Length && value[position] == (Byte)'#')
        {
            return DecodeNumericAttributeReference(value, index, destination);
        }

        // The per-byte machine accumulates at most 32 alphanumeric candidate bytes.
        var start = position;
        while (position < value.Length && position - start < 32 && IsAsciiAlphaNumeric(value[position]))
        {
            position++;
        }
        if (position == start)
        {
            Append(destination, "&"u8);
            return position;
        }

        var hasSemicolon = position < value.Length && value[position] == (Byte)';';
        var candidate = value[start..(position + (hasSemicolon ? 1 : 0))];
        Span<Byte> replacement = stackalloc Byte[8];
        var entityLength = Utf8HtmlEntityDecoder.WriteLongestSymbolUtf8(candidate, replacement, out var matchedLength);
        if (entityLength != 0 && candidate[matchedLength - 1] != (Byte)';')
        {
            var followerIndex = start + matchedLength;
            if (
                followerIndex < value.Length
                && (value[followerIndex] == (Byte)'=' || IsAsciiAlphaNumeric(value[followerIndex]))
            )
            {
                entityLength = 0;
            }
        }
        if (entityLength != 0)
        {
            Append(destination, replacement[..entityLength]);
            Append(destination, candidate[matchedLength..]);
        }
        else
        {
            Append(destination, "&"u8);
            Append(destination, candidate);
        }
        return position + (hasSemicolon ? 1 : 0);
    }

    private Int32 DecodeNumericAttributeReference(ReadOnlySpan<Byte> value, Int32 index, Utf8TokenBuffer destination)
    {
        // index points at '&', index + 1 at '#'.
        var position = index + 2;
        var isHex = position < value.Length && (value[position] | 0x20) == (Byte)'x';
        if (isHex)
        {
            position++;
        }
        var radix = isHex ? 16u : 10u;
        var digitsStart = position;
        var scalar = 0u;
        var overflow = false;
        while (position < value.Length)
        {
            var digit = (UInt32)(value[position] - (Byte)'0');
            if (digit > 9)
            {
                if (!isHex)
                {
                    break;
                }
                digit = (UInt32)((value[position] | 0x20) - (Byte)'a');
                if (digit > 5)
                {
                    break;
                }
                digit += 10;
            }
            if (!overflow && scalar <= (0x10FFFFu - digit) / radix)
            {
                scalar = scalar * radix + digit;
            }
            else
            {
                overflow = true;
            }
            position++;
        }
        if (position == digitsStart)
        {
            // "&#" or "&#x" without digits is flushed raw; a terminating ';' goes with it.
            var end = position < value.Length && value[position] == (Byte)';' ? position + 1 : position;
            Append(destination, value[index..end]);
            return end;
        }
        if (position < value.Length && value[position] == (Byte)';')
        {
            position++;
        }

        Span<Byte> replacement = stackalloc Byte[4];
        Int32 length;
        if (overflow)
        {
            length = WriteReplacementCharacter(replacement);
        }
        else
        {
            var code = (Int32)scalar;
            var mapped = Utf8HtmlEntityDecoder.GetSymbolCodeFromTable(code);
            code = mapped < 0 ? code : mapped;
            length = Utf8HtmlEntityDecoder.IsInvalidNumber(code)
                ? WriteReplacementCharacter(replacement)
                : WriteScalarUtf8(code, replacement);
        }
        Append(destination, replacement[..length]);
        return position;
    }

    private void BeginTag(Boolean isEndTag, Byte firstByte)
    {
        _isEndTag = isEndTag;
        if (_startTagSourceRangeSink is not null)
        {
            _currentTagSourceOffset = _lastLessThanSourceOffset;
        }
        _startTagEmitted = false;
        _captureStartTagAttributes = false;
        Clear(_name);
        Clear(_attributeName);
        Clear(_attributeValue);
        Clear(_seenAttributeNames);
        _seenCompactAttributeNames?.Reset();
        Utf8AttributeNameIndex.Reset(ref _seenAttributeIndex);
        _seenFallbackAttributeCount = 0;
        _tagNameIdentityCache.Reset();
        _attributeNameIdentityCache.Reset();
        _attributeCapture = AttributeCapture.Undecided;
        AppendTagName(firstByte);
        _state = State.TagName;
    }

    private void EmitTagStart()
    {
        if (_startTagEmitted || _isEndTag)
        {
            return;
        }

        _captureStartTagAttributes = (_sink.StartTag(CurrentTagName()) & Utf8HtmlStartTagCapture.Attributes) != 0;
        _startTagEmitted = true;
    }

    private void DecideAttributeCapture()
    {
        if (_attributeCapture != AttributeCapture.Undecided)
        {
            return;
        }

        if (!_captureStartTagAttributes || _isEndTag)
        {
            _attributeCapture = AttributeCapture.Discard;
            return;
        }
        EmitTagStart();
        var name = CurrentAttributeName();
        var capture = _sink.WantsAttribute(name);
        _attributeCapture =
            !TryAddSeenAttribute(name) ? AttributeCapture.Duplicate
            : capture ? AttributeCapture.Capture
            : AttributeCapture.Discard;
    }

    private void CommitAttribute()
    {
        // FinishTag calls this on every tag close whether or not an attribute is pending;
        // the commit path's struct locals must stay out of this frame so the nothing-pending
        // exit does not pay their stack clearing.
        if (_attributeName.WrittenCount != 0)
        {
            CommitPendingAttribute();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CommitPendingAttribute()
    {
        if (_isEndTag)
        {
            Clear(_attributeName);
            Clear(_attributeValue);
            _attributeCapture = AttributeCapture.Undecided;
            return;
        }
        DecideAttributeCapture();
        var name = CurrentAttributeName();
        if (_attributeCapture != AttributeCapture.Duplicate)
        {
            if (_attributeCapture == AttributeCapture.Capture)
            {
                var value = WrittenSpan(_attributeValue);
                var ampersand = IsNotConsumingCharacterReferences ? -1 : value.IndexOf((Byte)'&');
                if (ampersand < 0)
                {
                    _sink.Attribute(name, value);
                }
                else
                {
                    var decoded = _decodedAttributeValue ??= new(128);
                    DecodeAttributeValueReferences(value, ampersand, decoded);
                    _sink.Attribute(name, WrittenSpan(decoded));
                    Clear(decoded);
                }
            }
        }
        Clear(_attributeName);
        Clear(_attributeValue);
        _attributeNameIdentityCache.Reset();
        _attributeCapture = AttributeCapture.Undecided;
    }

    private Boolean TryAddSeenAttribute(Utf8HtmlName name)
    {
        if (name.Verbatim.Length <= 12 && name.Verbatim.IndexOf((Byte)'-') < 0 && name.TryGetCompactKey(out var key))
        {
            return (_seenCompactAttributeNames ??= new Utf8CompactAttributeNameSet()).TryAdd(key);
        }

        if (HasSeenFallbackAttribute(name))
        {
            return false;
        }

        var seenNames = _seenAttributeNames ??= new Utf8TokenBuffer(128);
        var nameOffset = seenNames.WrittenCount;
        Append(seenNames, name.Verbatim);
        Append(seenNames, (Byte)0);
        if (_seenAttributeIndex is not null)
        {
            Utf8AttributeNameIndex.Add(ref _seenAttributeIndex, name.SemanticHash, nameOffset);
        }
        _seenFallbackAttributeCount++;
        return true;
    }

    private Boolean HasSeenFallbackAttribute(Utf8HtmlName name)
    {
        var index = _seenAttributeIndex;
        if (index is not null)
        {
            return Utf8AttributeNameIndex.Contains(index, name, WrittenSpan(_seenAttributeNames));
        }

        var seen = WrittenSpan(_seenAttributeNames);
        while (!seen.IsEmpty)
        {
            var end = seen.IndexOf((Byte)0);
            if (end < 0)
            {
                return false;
            }

            if (name.SemanticEquals(seen[..end]))
            {
                return true;
            }

            seen = seen.Slice(end + 1);
        }

        if (_seenFallbackAttributeCount >= AttributeIndexPromotionThreshold)
        {
            Utf8AttributeNameIndex.Initialize(
                ref _seenAttributeIndex,
                WrittenSpan(_seenAttributeNames),
                _seenFallbackAttributeCount
            );
        }
        return false;
    }

    private void EmitDoctype(Boolean forceEofQuirks)
    {
        var source = _candidate.WrittenSpan;
        var index = 0;
        var quirks = false;
        var publicMissing = true;
        var systemMissing = true;
        var state = DoctypeState.BeforeName;
        Clear(_name);
        Clear(_doctypePublic);
        Clear(_doctypeSystem);

        while (index < source.Length)
        {
            var value = source[index++];
            switch (state)
            {
                case DoctypeState.BeforeName:
                    if (IsSpace(value))
                    {
                        break;
                    }

                    AppendReplacedNull(_name, value, lowerAscii: true);
                    state = DoctypeState.Name;
                    break;
                case DoctypeState.Name:
                    if (IsSpace(value))
                    {
                        state = DoctypeState.AfterName;
                    }
                    else
                    {
                        AppendReplacedNull(_name, value, lowerAscii: true);
                    }

                    break;
                case DoctypeState.AfterName:
                    if (IsSpace(value))
                    {
                        break;
                    }

                    index--;
                    if (ConsumeKeyword(source, ref index, "public"u8))
                    {
                        state = DoctypeState.AfterPublicKeyword;
                    }
                    else if (ConsumeKeyword(source, ref index, "system"u8))
                    {
                        state = DoctypeState.AfterSystemKeyword;
                    }
                    else
                    {
                        quirks = true;
                        state = DoctypeState.Bogus;
                    }
                    break;
                case DoctypeState.AfterPublicKeyword:
                    if (IsSpace(value))
                    {
                        state = DoctypeState.BeforePublicIdentifier;
                    }
                    else if (value is (Byte)'"' or (Byte)'\'')
                    {
                        publicMissing = false;
                        state =
                            value == (Byte)'"'
                                ? DoctypeState.PublicIdentifierDoubleQuoted
                                : DoctypeState.PublicIdentifierSingleQuoted;
                    }
                    else
                    {
                        quirks = true;
                        state = DoctypeState.Bogus;
                    }
                    break;
                case DoctypeState.BeforePublicIdentifier:
                    if (IsSpace(value))
                    {
                        break;
                    }

                    if (value is (Byte)'"' or (Byte)'\'')
                    {
                        publicMissing = false;
                        state =
                            value == (Byte)'"'
                                ? DoctypeState.PublicIdentifierDoubleQuoted
                                : DoctypeState.PublicIdentifierSingleQuoted;
                    }
                    else
                    {
                        quirks = true;
                        state = DoctypeState.Bogus;
                    }
                    break;
                case DoctypeState.PublicIdentifierDoubleQuoted:
                case DoctypeState.PublicIdentifierSingleQuoted:
                    var publicQuote = state == DoctypeState.PublicIdentifierDoubleQuoted ? (Byte)'"' : (Byte)'\'';
                    if (value == publicQuote)
                    {
                        state = DoctypeState.AfterPublicIdentifier;
                    }
                    else
                    {
                        AppendReplacedNull(DoctypePublic, value, lowerAscii: false);
                    }

                    break;
                case DoctypeState.AfterPublicIdentifier:
                    if (IsSpace(value))
                    {
                        state = DoctypeState.BetweenPublicAndSystemIdentifiers;
                    }
                    else if (value is (Byte)'"' or (Byte)'\'')
                    {
                        systemMissing = false;
                        state =
                            value == (Byte)'"'
                                ? DoctypeState.SystemIdentifierDoubleQuoted
                                : DoctypeState.SystemIdentifierSingleQuoted;
                    }
                    else
                    {
                        quirks = true;
                        state = DoctypeState.Bogus;
                    }
                    break;
                case DoctypeState.BetweenPublicAndSystemIdentifiers:
                    if (IsSpace(value))
                    {
                        break;
                    }

                    if (value is (Byte)'"' or (Byte)'\'')
                    {
                        systemMissing = false;
                        state =
                            value == (Byte)'"'
                                ? DoctypeState.SystemIdentifierDoubleQuoted
                                : DoctypeState.SystemIdentifierSingleQuoted;
                    }
                    else
                    {
                        quirks = true;
                        state = DoctypeState.Bogus;
                    }
                    break;
                case DoctypeState.AfterSystemKeyword:
                    if (IsSpace(value))
                    {
                        state = DoctypeState.BeforeSystemIdentifier;
                    }
                    else if (value is (Byte)'"' or (Byte)'\'')
                    {
                        systemMissing = false;
                        state =
                            value == (Byte)'"'
                                ? DoctypeState.SystemIdentifierDoubleQuoted
                                : DoctypeState.SystemIdentifierSingleQuoted;
                    }
                    else
                    {
                        quirks = true;
                        state = DoctypeState.Bogus;
                    }
                    break;
                case DoctypeState.BeforeSystemIdentifier:
                    if (IsSpace(value))
                    {
                        break;
                    }

                    if (value is (Byte)'"' or (Byte)'\'')
                    {
                        systemMissing = false;
                        state =
                            value == (Byte)'"'
                                ? DoctypeState.SystemIdentifierDoubleQuoted
                                : DoctypeState.SystemIdentifierSingleQuoted;
                    }
                    else
                    {
                        quirks = true;
                        state = DoctypeState.Bogus;
                    }
                    break;
                case DoctypeState.SystemIdentifierDoubleQuoted:
                case DoctypeState.SystemIdentifierSingleQuoted:
                    var systemQuote = state == DoctypeState.SystemIdentifierDoubleQuoted ? (Byte)'"' : (Byte)'\'';
                    if (value == systemQuote)
                    {
                        state = DoctypeState.AfterSystemIdentifier;
                    }
                    else
                    {
                        AppendReplacedNull(DoctypeSystem, value, lowerAscii: false);
                    }

                    break;
                case DoctypeState.AfterSystemIdentifier:
                    if (!IsSpace(value))
                    {
                        state = DoctypeState.Bogus;
                    }

                    break;
                case DoctypeState.Bogus:
                    break;
                default:
                    throw new InvalidOperationException($"Unknown DOCTYPE state: {state}");
            }
        }

        if (_name.WrittenCount == 0)
        {
            quirks = true;
        }

        if (
            state
            is DoctypeState.AfterPublicKeyword
                or DoctypeState.BeforePublicIdentifier
                or DoctypeState.PublicIdentifierDoubleQuoted
                or DoctypeState.PublicIdentifierSingleQuoted
                or DoctypeState.AfterSystemKeyword
                or DoctypeState.BeforeSystemIdentifier
                or DoctypeState.SystemIdentifierDoubleQuoted
                or DoctypeState.SystemIdentifierSingleQuoted
        )
        {
            quirks = true;
        }

        if (
            forceEofQuirks
            && state
                is DoctypeState.BeforeName
                    or DoctypeState.Name
                    or DoctypeState.AfterName
                    or DoctypeState.AfterPublicKeyword
                    or DoctypeState.BeforePublicIdentifier
                    or DoctypeState.PublicIdentifierDoubleQuoted
                    or DoctypeState.PublicIdentifierSingleQuoted
                    or DoctypeState.AfterPublicIdentifier
                    or DoctypeState.BetweenPublicAndSystemIdentifiers
                    or DoctypeState.AfterSystemKeyword
                    or DoctypeState.BeforeSystemIdentifier
                    or DoctypeState.SystemIdentifierDoubleQuoted
                    or DoctypeState.SystemIdentifierSingleQuoted
                    or DoctypeState.AfterSystemIdentifier
        )
        {
            quirks = true;
        }

        var token = new Utf8DoctypeToken(
            _name.WrittenSpan,
            WrittenSpan(_doctypePublic),
            publicMissing,
            WrittenSpan(_doctypeSystem),
            systemMissing,
            quirks
        );
        _sink.Doctype(in token);
        Clear(_candidate);
        Clear(_name);
        Clear(_doctypePublic);
        Clear(_doctypeSystem);
    }

    private void AppendReplacedNull(Utf8TokenBuffer destination, Byte value, Boolean lowerAscii)
    {
        if (value == 0)
        {
            AppendReplacement(destination);
        }
        else
        {
            Append(destination, lowerAscii ? AsciiLower(value) : value);
        }
    }

    private static Boolean ConsumeKeyword(ReadOnlySpan<Byte> source, ref Int32 index, ReadOnlySpan<Byte> keyword)
    {
        if (
            source.Length - index < keyword.Length
            || !StartsWithAsciiIgnoreCase(source.Slice(index, keyword.Length), keyword)
        )
        {
            return false;
        }

        index += keyword.Length;
        return true;
    }

    private void AppendReplacement(Utf8TokenBuffer destination) => Append(destination, "\uFFFD"u8);

    private void ProcessScript(Byte value, ref Boolean reconsume)
    {
        switch (_state)
        {
            case State.ScriptData:
                if (value == '<')
                {
                    _state = State.ScriptLessThan;
                }
                else
                {
                    EmitScriptByte(value);
                }

                break;
            case State.ScriptLessThan:
                if (value == '/')
                {
                    BeginScriptEndTag(State.ScriptEndTagName);
                }
                else if (value == '!')
                {
                    EmitText("<!"u8);
                    _state = State.ScriptEscapeStart;
                }
                else
                {
                    EmitText("<"u8);
                    Reconsume(ref reconsume, State.ScriptData);
                }
                break;
            case State.ScriptEscapeStart:
                if (value == '-')
                {
                    EmitByte(value);
                    _state = State.ScriptEscapeStartDash;
                }
                else
                {
                    Reconsume(ref reconsume, State.ScriptData);
                }

                break;
            case State.ScriptEscapeStartDash:
                if (value == '-')
                {
                    EmitByte(value);
                    _state = State.ScriptEscapedDashDash;
                }
                else
                {
                    Reconsume(ref reconsume, State.ScriptData);
                }

                break;
            case State.ScriptEscaped:
                if (value == '-')
                {
                    EmitByte(value);
                    _state = State.ScriptEscapedDash;
                }
                else if (value == '<')
                {
                    _state = State.ScriptEscapedLessThan;
                }
                else
                {
                    EmitScriptByte(value);
                }

                break;
            case State.ScriptEscapedDash:
                if (value == '-')
                {
                    EmitByte(value);
                    _state = State.ScriptEscapedDashDash;
                }
                else if (value == '<')
                {
                    _state = State.ScriptEscapedLessThan;
                }
                else
                {
                    EmitScriptByte(value);
                    _state = State.ScriptEscaped;
                }
                break;
            case State.ScriptEscapedDashDash:
                if (value == '-')
                {
                    EmitByte(value);
                }
                else if (value == '<')
                {
                    _state = State.ScriptEscapedLessThan;
                }
                else if (value == '>')
                {
                    EmitByte(value);
                    _state = State.ScriptData;
                }
                else
                {
                    EmitScriptByte(value);
                    _state = State.ScriptEscaped;
                }
                break;
            case State.ScriptEscapedLessThan:
                if (value == '/')
                {
                    BeginScriptEndTag(State.ScriptEscapedEndTagName);
                }
                else if (IsAsciiLetter(value))
                {
                    EmitText("<"u8);
                    Clear(_candidate);
                    Append(_candidate, AsciiLower(value));
                    EmitByte(value);
                    _state = State.ScriptDoubleEscapeStart;
                }
                else
                {
                    EmitText("<"u8);
                    Reconsume(ref reconsume, State.ScriptEscaped);
                }
                break;
            case State.ScriptEndTagName:
                ProcessScriptEndTag(value, State.ScriptData, ref reconsume);
                break;
            case State.ScriptEscapedEndTagName:
                ProcessScriptEndTag(value, State.ScriptEscaped, ref reconsume);
                break;
            case State.ScriptDoubleEscapeStart:
                if (IsAsciiLetter(value))
                {
                    Append(_candidate, AsciiLower(value));
                    EmitByte(value);
                }
                else if (IsTagDelimiter(value))
                {
                    var script = _candidate.WrittenSpan.SequenceEqual("script"u8);
                    Clear(_candidate);
                    EmitByte(value);
                    _state = script ? State.ScriptDoubleEscaped : State.ScriptEscaped;
                }
                else
                {
                    Clear(_candidate);
                    Reconsume(ref reconsume, State.ScriptEscaped);
                }
                break;
            case State.ScriptDoubleEscaped:
                if (value == '-')
                {
                    EmitByte(value);
                    _state = State.ScriptDoubleEscapedDash;
                }
                else if (value == '<')
                {
                    EmitByte(value);
                    _state = State.ScriptDoubleEscapedLessThan;
                }
                else
                {
                    EmitScriptByte(value);
                }

                break;
            case State.ScriptDoubleEscapedDash:
                if (value == '-')
                {
                    EmitByte(value);
                    _state = State.ScriptDoubleEscapedDashDash;
                }
                else if (value == '<')
                {
                    EmitByte(value);
                    _state = State.ScriptDoubleEscapedLessThan;
                }
                else
                {
                    EmitScriptByte(value);
                    _state = State.ScriptDoubleEscaped;
                }
                break;
            case State.ScriptDoubleEscapedDashDash:
                if (value == '-')
                {
                    EmitByte(value);
                }
                else if (value == '<')
                {
                    EmitByte(value);
                    _state = State.ScriptDoubleEscapedLessThan;
                }
                else if (value == '>')
                {
                    EmitByte(value);
                    _state = State.ScriptData;
                }
                else
                {
                    EmitScriptByte(value);
                    _state = State.ScriptDoubleEscaped;
                }
                break;
            case State.ScriptDoubleEscapedLessThan:
                if (value == '/')
                {
                    EmitByte(value);
                    Clear(_candidate);
                    _state = State.ScriptDoubleEscapeEnd;
                }
                else
                {
                    Reconsume(ref reconsume, State.ScriptDoubleEscaped);
                }

                break;
            case State.ScriptDoubleEscapeEnd:
                if (IsAsciiLetter(value))
                {
                    Append(_candidate, AsciiLower(value));
                    EmitByte(value);
                }
                else if (IsTagDelimiter(value))
                {
                    var script = _candidate.WrittenSpan.SequenceEqual("script"u8);
                    Clear(_candidate);
                    EmitByte(value);
                    _state = script ? State.ScriptEscaped : State.ScriptDoubleEscaped;
                }
                else
                {
                    Clear(_candidate);
                    Reconsume(ref reconsume, State.ScriptDoubleEscaped);
                }
                break;
        }
    }

    private void ProcessScriptInput<TTrust>(Byte value, ReadOnlySpan<Byte> utf8, ref Int32 index)
        where TTrust : struct, IInputTrustPolicy
    {
        var reconsume = true;
        while (reconsume)
        {
            reconsume = false;
            RecordState((Int32)_state, 1);
            ProcessScript(value, ref reconsume);
        }

        if (
            _state is not (State.ScriptEscaped or State.ScriptDoubleEscaped)
            || _pendingCarriageReturn
            || _textUtf8CarryLength != 0
        )
        {
            return;
        }

        var remaining = utf8[index..];
        var run = _captureText
            ? IndexOfCaptureStop<TTrust>(remaining, EscapedScriptTextTerminators, EscapedScriptTextArbitraryAllowed)
            : remaining.IndexOfAny((Byte)'<', (Byte)'-');
        if (run < 0)
        {
            run = remaining.Length;
        }
        if (run == 0)
        {
            return;
        }

        RecordState((Int32)_state, run);
        if (_captureText)
        {
            EmitText(remaining[..run]);
        }
        index += run;
    }

    private void BeginScriptEndTag(State state)
    {
        Clear(_candidate);
        Append(_candidate, "</"u8);
        _state = state;
    }

    private void ProcessScriptEndTag(Byte value, State fallback, ref Boolean reconsume)
    {
        if (IsAsciiLetter(value))
        {
            Append(_candidate, value);
            return;
        }
        var candidate = _candidate.WrittenSpan;
        if (RawCandidateMatches() && IsTagDelimiter(value))
        {
            Clear(_name);
            _tagNameIdentityCache.Reset();
            AppendTagName(candidate.Slice(2));
            Clear(_candidate);
            _isEndTag = true;
            _rawEndTag = null;
            if (value == '>')
            {
                FinishTag(false);
            }
            else if (value == '/')
            {
                _state = State.SelfClosingStartTag;
            }
            else
            {
                _state = State.BeforeAttributeName;
            }

            return;
        }
        EmitText(_candidate.WrittenSpan);
        Clear(_candidate);
        Reconsume(ref reconsume, fallback);
    }

    private void EmitScriptByte(Byte value)
    {
        if (value == 0)
        {
            EmitReplacementCharacter();
        }
        else
        {
            EmitByte(value);
        }
    }

    private static Boolean IsScriptState(State state) =>
        state is >= State.ScriptData and <= State.ScriptDoubleEscapeEnd;

    private void FinishTag(Boolean selfClosing)
    {
        if (_seenAttributeIndex is null && _seenFallbackAttributeCount < AttributeIndexPromotionThreshold)
        {
            CommitAttribute();
            FinishTagCore(selfClosing);
            _seenFallbackAttributeCount = 0;
            return;
        }

        try
        {
            CommitAttribute();
            FinishTagCore(selfClosing);
        }
        finally
        {
            Utf8AttributeNameIndex.Reset(ref _seenAttributeIndex);
            _seenFallbackAttributeCount = 0;
        }
    }

    private void FinishTagCore(Boolean selfClosing)
    {
        if (_isEndTag)
        {
            _sink.EndTag(CurrentTagName());
            RefreshCapture();
            _rawEndTag = null;
        }
        else
        {
            EmitTagStart();
            _startTagSourceRangeSink?.StartTagSourceRange(_currentTagSourceOffset, _currentSourceOffset);
            _sink.StartTagEnd(selfClosing);
            RefreshCapture();
            // In HTML, the trailing solidus does not make a non-void element self-closing.
            // Tree construction controls the mode in the DOM path; the standalone path must
            // therefore still infer text modes for e.g. <textarea/> and <plaintext/>.
            if (!IsModeControlledExternally)
            {
                var name = CurrentTagName();
                if (name.TryGetCompactKey(out var key))
                {
                    switch (key)
                    {
                        case TitleKey:
                            _rawEndTag = "rcdata:title";
                            break;
                        case TextareaKey:
                            _rawEndTag = "rcdata:textarea";
                            break;
                        case StyleKey:
                            _rawEndTag = "style";
                            break;
                        case XmpKey:
                            _rawEndTag = "xmp";
                            break;
                        case IframeKey:
                            _rawEndTag = "iframe";
                            break;
                        case NoEmbedKey:
                            _rawEndTag = "noembed";
                            break;
                        case NoFramesKey:
                            _rawEndTag = "noframes";
                            break;
                        case ScriptKey:
                            _rawEndTag = "script";
                            _state = State.ScriptData;
                            break;
                        case PlaintextKey:
                            _state = State.Plaintext;
                            break;
                    }
                }
            }
        }
        Clear(_name);
        _isEndTag = false;
        _startTagEmitted = false;
        if (_state is not State.Plaintext and not State.ScriptData)
        {
            _state = _rawEndTag is null ? State.Data : State.RawText;
        }
    }

    // Every start tag used to pay for a compact-key computation just to discover it is not
    private Boolean RawCandidateMatches()
    {
        if (_rawEndTag is null)
        {
            return false;
        }

        var expected = _rawEndTag.StartsWith("rcdata:", StringComparison.Ordinal)
            ? _rawEndTag.AsSpan(7)
            : _rawEndTag.AsSpan();

        if (_candidate.WrittenCount != expected.Length + 2)
        {
            return false;
        }

        var candidate = _candidate.WrittenSpan.Slice(2);
        for (var index = 0; index < candidate.Length; index++)
        {
            if (AsciiLower(candidate[index]) != AsciiLower((Byte)expected[index]))
            {
                return false;
            }
        }
        return true;
    }

    private Boolean IsRcData() => _rawEndTag?.StartsWith("rcdata:", StringComparison.Ordinal) == true;

    private void RefreshCapture() => _captureText = (_sink.Capture & Utf8HtmlTokenCapture.Text) != 0;

    private void EmitText(ReadOnlySpan<Byte> utf8)
    {
        if (_captureText)
        {
            _sink.Text(utf8);
        }
    }

    private void EmitReplacementCharacter() => EmitText("\uFFFD"u8);

    private void EmitCDataText(ReadOnlySpan<Byte> utf8)
    {
        if (!SkipCDATA)
        {
            EmitText(utf8);
        }
    }

    private void EmitCDataByte(Byte value)
    {
        if (!SkipCDATA)
        {
            EmitByte(value);
        }
    }

    private void EmitByte(Byte value)
    {
        if (!_captureText)
        {
            return;
        }
        if (_textUtf8CarryLength != 0)
        {
            _textUtf8Carry |= (UInt32)value << (_textUtf8CarryLength++ * 8);
            if (_textUtf8CarryLength == _textUtf8ExpectedLength)
            {
                Span<Byte> scalar = stackalloc Byte[4];
                for (var index = 0; index < _textUtf8CarryLength; index++)
                {
                    scalar[index] = (Byte)(_textUtf8Carry >> (index * 8));
                }

                EmitText(scalar[.._textUtf8CarryLength]);
                _textUtf8Carry = 0;
                _textUtf8CarryLength = 0;
                _textUtf8ExpectedLength = 0;
            }
            return;
        }
        if (value >= 0x80)
        {
            _textUtf8Carry = value;
            _textUtf8CarryLength = 1;
            _textUtf8ExpectedLength = Utf8SequenceLength(value);
            return;
        }
        Span<Byte> single = stackalloc Byte[1];
        single[0] = value;
        EmitText(single);
    }

    private void Reconsume(ref Boolean reconsume, State state)
    {
        _state = state;
        reconsume = true;
        _reconsumes++;
    }

    private void Append(Utf8TokenBuffer buffer, Byte value)
    {
        if (TResourceLimits.Enabled)
        {
            EnsureBufferedTokenCapacity(1);
        }
        buffer.Append(value);
        if (TResourceLimits.Enabled)
        {
            ObserveBufferAppend(1);
        }
    }

    private void Append(Utf8TokenBuffer buffer, ReadOnlySpan<Byte> value)
    {
        if (TResourceLimits.Enabled)
        {
            EnsureBufferedTokenCapacity(value.Length);
        }
        buffer.Append(value);
        if (TResourceLimits.Enabled)
        {
            ObserveBufferAppend(value.Length);
        }
    }

    private void ObserveBufferAppend(Int32 count)
    {
        _bufferedTokenBytes += count;
        if (_bufferedTokenBytes > _maximumBufferedTokenBytes)
        {
            _maximumBufferedTokenBytes = (Int32)Math.Min(_bufferedTokenBytes, Int32.MaxValue);
        }
    }

    private void EnsureBufferedTokenCapacity(Int32 additional)
    {
        var observed = SaturatingAdd(_bufferedTokenBytes, additional);
        if (observed > _maximumBufferedTokenBytesAllowed)
        {
            ThrowLimitExceeded(HtmlStreamingLimit.BufferedTokenBytes, _maximumBufferedTokenBytesAllowed, observed);
        }
    }

    private static ReadOnlySpan<Byte> WrittenSpan(Utf8TokenBuffer? buffer) =>
        buffer is null ? ReadOnlySpan<Byte>.Empty : buffer.WrittenSpan;

    private void Clear(Utf8TokenBuffer? buffer)
    {
        if (buffer is null)
        {
            return;
        }

        if (TResourceLimits.Enabled)
        {
            _bufferedTokenBytes -= buffer.WrittenCount;
        }
        buffer.ResetWrittenCount();
    }

    private static Int64 SaturatingAdd(Int64 value, Int32 additional) =>
        value > Int64.MaxValue - additional ? Int64.MaxValue : value + additional;

    private static void ThrowLimitExceeded(HtmlStreamingLimit limit, Int64 allowed, Int64 observed) =>
        throw new HtmlStreamingLimitExceededException(limit, allowed, observed);

    private static Int32 Utf8SequenceLength(Byte lead) =>
        lead switch
        {
            < 0x80 => 1,
            < 0xE0 => 2,
            < 0xF0 => 3,
            < 0xF8 => 4,
            _ => 1,
        };

    private void AppendTagName(Byte value) => Append(_name, value);

    private void AppendTagName(ReadOnlySpan<Byte> value) => Append(_name, value);

    private void AppendTagNameReplacedNull(Byte value)
    {
        if (value == 0)
        {
            AppendTagName("\uFFFD"u8);
        }
        else
        {
            AppendTagName(value);
        }
    }

    private Utf8HtmlName CurrentTagName() => new(_name.WrittenSpan, ref _tagNameIdentityCache);

    private Utf8HtmlName CurrentAttributeName() => new(_attributeName.WrittenSpan, ref _attributeNameIdentityCache);

    private static Boolean IsTagTailState(State state) =>
        state
            is State.BeforeAttributeName
                or State.AttributeName
                or State.AfterAttributeName
                or State.BeforeAttributeValue
                or State.AttributeValueDoubleQuoted
                or State.AttributeValueSingleQuoted
                or State.AttributeValueUnquoted
                or State.AfterAttributeValueQuoted
                or State.SelfClosingStartTag;

    private Int32 ScanTagTail<TMetrics, TTrust, TCapture>(
        ReadOnlySpan<Byte> utf8,
        Int64 sourceOffset,
        Boolean trackSourceRanges,
        Boolean yieldOnRequest
    )
        where TMetrics : struct, IStateMetricsPolicy
        where TTrust : struct, IInputTrustPolicy
        where TCapture : struct, IAttributeCapturePolicy
    {
        // Threaded form of the tag-tail states: the entry state dispatches once, control then
        // transfers directly between the labelled constructs, and the current state lives in
        // the program counter instead of the _state field until the span runs out or the tag
        // finishes. Discarded tails dominate dense markup, so the per-transition dispatch and
        // field traffic this removes is multiplied by the attribute count of the whole input.
        // The CaptureOn instantiation runs the same shape over captured start tags, folding the
        // name/value appends and commit calls into the transfers; bytes the scanner cannot
        // resolve locally ('\0' replacement, '\r' normalization inside values, unvalidated
        // non-ASCII) hand back to the per-byte fallback by writing _state and returning the
        // consumed count, after which the outer loop re-enters the scanner.
        var index = 0;
        // A callback may request a yield while the old per-byte machine is still reconsuming the
        // current delimiter. Finish that transition before returning at the same observable boundary.
        var yieldAfterTransition = false;
        Byte value;
        switch (_state)
        {
            case State.BeforeAttributeName:
                goto BeforeAttributeName;
            case State.AttributeName:
                goto AttributeName;
            case State.AfterAttributeName:
                goto AfterAttributeName;
            case State.BeforeAttributeValue:
                goto BeforeAttributeValue;
            case State.AttributeValueDoubleQuoted:
                goto AttributeValueDoubleQuoted;
            case State.AttributeValueSingleQuoted:
                goto AttributeValueSingleQuoted;
            case State.AttributeValueUnquoted:
                goto AttributeValueUnquoted;
            case State.AfterAttributeValueQuoted:
                goto AfterAttributeValueQuoted;
            case State.SelfClosingStartTag:
                goto SelfClosingStartTag;
            default:
                return 0;
        }

        BeforeAttributeName:
        while (true)
        {
            if ((UInt32)index >= (UInt32)utf8.Length)
            {
                _state = State.BeforeAttributeName;
                return index;
            }
            value = utf8[index];
            RecordState<TMetrics>((Int32)State.BeforeAttributeName, 1);
            if (IsSpace(value))
            {
                index++;
                continue;
            }
            if (value == (Byte)'>')
            {
                _state = State.BeforeAttributeName;
                FinishScannedTag(ref index, selfClosing: false, sourceOffset, trackSourceRanges);
                return index;
            }
            if (value == (Byte)'/')
            {
                index++;
                if (yieldAfterTransition)
                {
                    _state = State.SelfClosingStartTag;
                    return index;
                }
                goto SelfClosingStartTag;
            }
            if (TCapture.Enabled)
            {
                if (value == 0 || (TTrust.StopAtNonAscii && value >= 0x80))
                {
                    // '\0' starts the name with a replacement character and unvalidated
                    // non-ASCII must bounce to the caller: per-byte fallback for both.
                    _state = State.BeforeAttributeName;
                    return index;
                }
                Clear(_attributeName);
                Clear(_attributeValue);
                _attributeNameIdentityCache.Reset();
                Append(_attributeName, value);
                index++;
            }
            if (yieldAfterTransition)
            {
                _state = State.AttributeName;
                return index;
            }
            goto AttributeName;
        }

        AttributeName:
        {
            var remaining = utf8[index..];
            var run = TCapture.Enabled
                ? IndexOfCaptureStop<TTrust>(remaining, AttributeNameTerminators, AttributeNameArbitraryAllowed)
                : remaining.IndexOfAny(DiscardedAttributeNameTerminators);
            if (run < 0)
            {
                RecordState<TMetrics>((Int32)State.AttributeName, remaining.Length);
                if (TCapture.Enabled && !remaining.IsEmpty)
                {
                    Append(_attributeName, remaining);
                }
                _state = State.AttributeName;
                return utf8.Length;
            }
            value = remaining[run];
            if (TCapture.Enabled)
            {
                if (run > 0)
                {
                    Append(_attributeName, remaining[..run]);
                }
                if (value == 0 || (TTrust.StopAtNonAscii && value >= 0x80))
                {
                    RecordState<TMetrics>((Int32)State.AttributeName, run);
                    _state = State.AttributeName;
                    return index + run;
                }
            }
            RecordState<TMetrics>((Int32)State.AttributeName, run + 1);
            index += run;
            if (value == (Byte)'=')
            {
                index++;
                if (TCapture.Enabled)
                {
                    DecideAttributeCapture();
                    if (yieldOnRequest && _yieldRequested)
                    {
                        _state = State.BeforeAttributeValue;
                        return index;
                    }
                }
                goto BeforeAttributeValue;
            }
            if (IsSpace(value))
            {
                index++;
                goto AfterAttributeName;
            }
            // '/' or '>': the pending attribute commits, then the byte is reconsumed by the
            // attribute-start handler, as in the general machine.
            if (TCapture.Enabled)
            {
                CommitAttribute();
                yieldAfterTransition = yieldOnRequest && _yieldRequested;
            }
            goto BeforeAttributeName;
        }

        AfterAttributeName:
        while (true)
        {
            if ((UInt32)index >= (UInt32)utf8.Length)
            {
                _state = State.AfterAttributeName;
                return index;
            }
            value = utf8[index];
            RecordState<TMetrics>((Int32)State.AfterAttributeName, 1);
            if (IsSpace(value))
            {
                index++;
                continue;
            }
            if (value == (Byte)'=')
            {
                index++;
                if (TCapture.Enabled)
                {
                    DecideAttributeCapture();
                    if (yieldOnRequest && _yieldRequested)
                    {
                        _state = State.BeforeAttributeValue;
                        return index;
                    }
                }
                goto BeforeAttributeValue;
            }
            // Anything else ends the name-only attribute; the byte is reconsumed as an
            // attribute-name starter (or '/', '>').
            if (TCapture.Enabled)
            {
                CommitAttribute();
                yieldAfterTransition = yieldOnRequest && _yieldRequested;
            }
            goto BeforeAttributeName;
        }

        BeforeAttributeValue:
        while (true)
        {
            if ((UInt32)index >= (UInt32)utf8.Length)
            {
                _state = State.BeforeAttributeValue;
                return index;
            }
            value = utf8[index];
            RecordState<TMetrics>((Int32)State.BeforeAttributeValue, 1);
            if (IsSpace(value))
            {
                index++;
                continue;
            }
            if (value == (Byte)'"')
            {
                index++;
                goto AttributeValueDoubleQuoted;
            }
            if (value == (Byte)'\'')
            {
                index++;
                goto AttributeValueSingleQuoted;
            }
            if (value == (Byte)'>')
            {
                // FinishTag commits the pending missing-value attribute before closing.
                _state = State.BeforeAttributeValue;
                FinishScannedTag(ref index, selfClosing: false, sourceOffset, trackSourceRanges);
                return index;
            }
            goto AttributeValueUnquoted;
        }

        AttributeValueDoubleQuoted:
        {
            var remaining = utf8[index..];
            if (TCapture.Enabled && _attributeCapture == AttributeCapture.Capture)
            {
                var run = IndexOfCaptureStop<TTrust>(
                    remaining,
                    DoubleQuotedAttributeValueTerminators,
                    DoubleQuotedAttributeValueArbitraryAllowed
                );
                if (run < 0)
                {
                    RecordState<TMetrics>((Int32)State.AttributeValueDoubleQuoted, remaining.Length);
                    if (!remaining.IsEmpty)
                    {
                        Append(AttributeValue, remaining);
                    }
                    _state = State.AttributeValueDoubleQuoted;
                    return utf8.Length;
                }
                if (run > 0)
                {
                    Append(AttributeValue, remaining[..run]);
                }
                if (remaining[run] != (Byte)'"')
                {
                    // '\0' replacement, '\r' normalization, or unvalidated non-ASCII.
                    RecordState<TMetrics>((Int32)State.AttributeValueDoubleQuoted, run);
                    _state = State.AttributeValueDoubleQuoted;
                    return index + run;
                }
                RecordState<TMetrics>((Int32)State.AttributeValueDoubleQuoted, run + 1);
                index += run + 1;
                goto AfterAttributeValueQuoted;
            }
            else
            {
                var run = remaining.IndexOf((Byte)'"');
                if (run < 0)
                {
                    RecordState<TMetrics>((Int32)State.AttributeValueDoubleQuoted, remaining.Length);
                    _state = State.AttributeValueDoubleQuoted;
                    return utf8.Length;
                }
                RecordState<TMetrics>((Int32)State.AttributeValueDoubleQuoted, run + 1);
                index += run + 1;
                goto AfterAttributeValueQuoted;
            }
        }

        AttributeValueSingleQuoted:
        {
            var remaining = utf8[index..];
            if (TCapture.Enabled && _attributeCapture == AttributeCapture.Capture)
            {
                var run = IndexOfCaptureStop<TTrust>(
                    remaining,
                    SingleQuotedAttributeValueTerminators,
                    SingleQuotedAttributeValueArbitraryAllowed
                );
                if (run < 0)
                {
                    RecordState<TMetrics>((Int32)State.AttributeValueSingleQuoted, remaining.Length);
                    if (!remaining.IsEmpty)
                    {
                        Append(AttributeValue, remaining);
                    }
                    _state = State.AttributeValueSingleQuoted;
                    return utf8.Length;
                }
                if (run > 0)
                {
                    Append(AttributeValue, remaining[..run]);
                }
                if (remaining[run] != (Byte)'\'')
                {
                    RecordState<TMetrics>((Int32)State.AttributeValueSingleQuoted, run);
                    _state = State.AttributeValueSingleQuoted;
                    return index + run;
                }
                RecordState<TMetrics>((Int32)State.AttributeValueSingleQuoted, run + 1);
                index += run + 1;
                goto AfterAttributeValueQuoted;
            }
            else
            {
                var run = remaining.IndexOf((Byte)'\'');
                if (run < 0)
                {
                    RecordState<TMetrics>((Int32)State.AttributeValueSingleQuoted, remaining.Length);
                    _state = State.AttributeValueSingleQuoted;
                    return utf8.Length;
                }
                RecordState<TMetrics>((Int32)State.AttributeValueSingleQuoted, run + 1);
                index += run + 1;
                goto AfterAttributeValueQuoted;
            }
        }

        AttributeValueUnquoted:
        {
            var remaining = utf8[index..];
            Int32 run;
            if (TCapture.Enabled && _attributeCapture == AttributeCapture.Capture)
            {
                run = IndexOfCaptureStop<TTrust>(
                    remaining,
                    UnquotedAttributeValueTerminators,
                    UnquotedAttributeValueArbitraryAllowed
                );
                if (run < 0)
                {
                    RecordState<TMetrics>((Int32)State.AttributeValueUnquoted, remaining.Length);
                    if (!remaining.IsEmpty)
                    {
                        Append(AttributeValue, remaining);
                    }
                    _state = State.AttributeValueUnquoted;
                    return utf8.Length;
                }
                value = remaining[run];
                if (run > 0)
                {
                    Append(AttributeValue, remaining[..run]);
                }
                if (value == 0 || (TTrust.StopAtNonAscii && value >= 0x80))
                {
                    RecordState<TMetrics>((Int32)State.AttributeValueUnquoted, run);
                    _state = State.AttributeValueUnquoted;
                    return index + run;
                }
            }
            else
            {
                run = remaining.IndexOfAny(DiscardedUnquotedAttributeValueTerminators);
                if (run < 0)
                {
                    RecordState<TMetrics>((Int32)State.AttributeValueUnquoted, remaining.Length);
                    _state = State.AttributeValueUnquoted;
                    return utf8.Length;
                }
                value = remaining[run];
            }
            RecordState<TMetrics>((Int32)State.AttributeValueUnquoted, run + 1);
            index += run;
            if (value == (Byte)'>')
            {
                // FinishTag commits the pending attribute before closing.
                _state = State.AttributeValueUnquoted;
                FinishScannedTag(ref index, selfClosing: false, sourceOffset, trackSourceRanges);
                return index;
            }
            // Whitespace ends the unquoted value.
            if (TCapture.Enabled)
            {
                CommitAttribute();
            }
            index++;
            if (yieldOnRequest && _yieldRequested)
            {
                _state = State.BeforeAttributeName;
                return index;
            }
            goto BeforeAttributeName;
        }

        AfterAttributeValueQuoted:
        {
            if ((UInt32)index >= (UInt32)utf8.Length)
            {
                _state = State.AfterAttributeValueQuoted;
                return index;
            }
            value = utf8[index];
            RecordState<TMetrics>((Int32)State.AfterAttributeValueQuoted, 1);
            if (TCapture.Enabled)
            {
                // The general machine commits the closed value before dispatching on the byte
                // after the quote; a span ending here defers the commit the same way.
                CommitAttribute();
            }
            if (IsSpace(value))
            {
                index++;
                if (yieldOnRequest && _yieldRequested)
                {
                    _state = State.BeforeAttributeName;
                    return index;
                }
                goto BeforeAttributeName;
            }
            if (value == (Byte)'/')
            {
                index++;
                if (yieldOnRequest && _yieldRequested)
                {
                    _state = State.SelfClosingStartTag;
                    return index;
                }
                goto SelfClosingStartTag;
            }
            if (value == (Byte)'>')
            {
                _state = State.AfterAttributeValueQuoted;
                FinishScannedTag(ref index, selfClosing: false, sourceOffset, trackSourceRanges);
                return index;
            }
            if (yieldOnRequest && _yieldRequested)
            {
                _state = State.BeforeAttributeName;
                return index;
            }
            goto BeforeAttributeName;
        }

        SelfClosingStartTag:
        {
            if ((UInt32)index >= (UInt32)utf8.Length)
            {
                _state = State.SelfClosingStartTag;
                return index;
            }
            value = utf8[index];
            RecordState<TMetrics>((Int32)State.SelfClosingStartTag, 1);
            if (value == (Byte)'>')
            {
                _state = State.SelfClosingStartTag;
                FinishScannedTag(ref index, selfClosing: true, sourceOffset, trackSourceRanges);
                return index;
            }
            goto BeforeAttributeName;
        }
    }

    private void FinishScannedTag(ref Int32 index, Boolean selfClosing, Int64 sourceOffset, Boolean trackSourceRanges)
    {
        index++;
        if (trackSourceRanges)
        {
            _currentSourceOffset = sourceOffset + index;
        }
        FinishTag(selfClosing);
    }

    private Boolean IsAttributeReturnState() => _returnState is not State.Data and not State.RawText;

    private static Boolean StartsWithAsciiIgnoreCase(ReadOnlySpan<Byte> expected, ReadOnlySpan<Byte> candidate)
    {
        if (candidate.Length > expected.Length)
        {
            return false;
        }

        for (var i = 0; i < candidate.Length; i++)
        {
            if (AsciiLower(expected[i]) != AsciiLower(candidate[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static Boolean IsSpace(Byte value) => value is 0x09 or 0x0A or 0x0C or 0x0D or 0x20;

    private static Boolean IsAsciiLetter(Byte value) =>
        (UInt32)(value - 'A') <= 'Z' - 'A' || (UInt32)(value - 'a') <= 'z' - 'a';

    private static Boolean IsAsciiAlphaNumeric(Byte value) => IsAsciiLetter(value) || (UInt32)(value - '0') <= 9;

    private static Boolean IsTagDelimiter(Byte value) => value is (Byte)'>' or (Byte)'/' || IsSpace(value);

    private static Byte AsciiLower(Byte value) => Utf8NameHash.ToLowerAscii(value);

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The tokenizer is already complete.");
        }
    }
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
