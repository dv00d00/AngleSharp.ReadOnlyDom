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
    /// The normalized-input offset before which no future tag edit can land. While a tag is open,
    /// the offset pins to its '&lt;'; every insertion, replacement, and separator look-back sits at or above
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
            var wantsEndTagRange = _startTagSourceRangeSink?.WantsEndTagSourceRanges == true;
            switch (_state)
            {
                case State.TagOpen:
                    return _lastLessThanSourceOffset;
                case State.EndTagOpen:
                case State.RawLessThan:
                case State.RawEndTagOpen:
                case State.RawEndTagName:
                case State.ScriptLessThan:
                case State.ScriptEndTagName:
                case State.ScriptEscapedLessThan:
                case State.ScriptEscapedEndTagName:
                    return wantsEndTagRange ? _lastLessThanSourceOffset : _currentSourceOffset;
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
                    return _isEndTag && !wantsEndTagRange ? _currentSourceOffset : _currentTagSourceOffset;
                case State.CharacterReference:
                    // A reference inside a captured attribute value keeps the start tag open.
                    return IsTagTailState(_returnState) && (!_isEndTag || wantsEndTagRange)
                        ? _currentTagSourceOffset
                        : _currentSourceOffset;
                default:
                    // Comments, doctypes, raw text, and script data never produce edits.
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
        Byte stopByte = 0;
        
        try
        {
            while (index < utf8.Length)
            {
                if (_state == State.TagName)
                {
                    var remaining = utf8.Slice(index);
                    var stop = IndexOfTagNameStop<TTrust>(remaining);
                    var run = stop < 0 ? remaining.Length : stop;

                    if (run > 0)
                    {
                        RecordState<TMetrics>((Int32)_state, run);
                        AppendTagName(remaining[..run]);
                        index += run;
                        if (stop < 0)
                        {
                            // The name continues past this span; nothing to fuse.
                            continue;
                        }
                    }

                    stopByte = remaining[run];
                }

                if (!_pendingCarriageReturn)
                {
                    if (_state == State.TagName)
                    {
                        // Tag-name stop fusion. The byte that ended the name is in-span and its entire
                        // effect in TagName state is one of three transitions, so take them here instead
                        // of surrendering the byte to the outer else-if chain, the PerByte prologue,
                        // Process's state switch and ProcessTagState's - the per-transition round-trip
                        // PR #66 removed for the tag tail but never for the name-to-tail boundary.
                        // Excluded on purpose: '\0' (becomes a replacement character), '\r' (starts CR
                        // normalization), unvalidated non-ASCII (must bounce to the caller), and a
                        // pending CR (the next byte belongs to the normalizer, not to this state).

                        if (stopByte is (Byte)'\t' or (Byte)'\n' or (Byte)'\f' or (Byte)' ' or (Byte)'/' or (Byte)'>')
                        {
                            index++;
                            RecordFusedTagNameStopIf<TMetrics>();
                            if (trackSourceRanges)
                            {
                                _currentSourceOffset = sourceBase + index;
                            }
                            if (stopByte == (Byte)'>')
                            {
                                FinishTag(selfClosing: false);
                            }
                            else
                            {
                                // Mirrors the ProcessTagState TagName arm: the start tag is emitted
                                // before the state changes, and end tags no-op inside EmitTagStart.
                                EmitTagStart();
                                _state = stopByte == (Byte)'/' ? State.SelfClosingStartTag : State.BeforeAttributeName;
                            }
                            if (yieldOnRequest && _yieldRequested)
                            {
                                return index;
                            }
                            continue;
                        }
                    }
                    else if ((_isEndTag || _startTagEmitted) && IsTagTailState(_state))
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
                    // Data only. Plaintext shares the shape but never occurs in real documents
                    // (zero instances across the 47-document corpus), so it sits in a cold arm
                    // below rather than costing this path a state test per text run.
                    else if (_state == State.Data && _textUtf8CarryLength == 0)
                    {
                        var remaining = utf8.Slice(index);
                        Int32 run;
                        // Minified markup is mostly adjacent tags, so data state is entered standing on
                        // '<' more often than not. Both searches below would scan for a byte that is already
                        // under the cursor and return 0 - '<' terminates the capture set as well - so
                        // test the first byte instead of paying a vectorized call to rediscover it.
                        // The fusion path below re-tests the same condition and owns the short-span case.
                        if (remaining[0] == (Byte)'<')
                        {
                            run = 0;
                        }
                        else if (!_captureText)
                        {
                            // Discarded text may swallow arbitrary bytes raw - nothing observes it.
                            run = remaining.IndexOf((Byte)'<');
                        }
                        else
                        {
                            run = IndexOfCaptureStop<TTrust>(remaining, DataTextTerminators, DataTextArbitraryAllowed);
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
                                
                                if (RawTextEnabled)
                                {
                                    EmitRawText(sourceBase + index, utf8.Slice(index, run), CurrentRawTextType());
                                }
                                
                                if (yieldOnRequest && _yieldRequested)
                                {
                                    index += run;
                                    return index;
                                }
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
                        if (remaining[0] == (Byte)'<' && remaining.Length >= 2)
                        {
                            Int32 fused;
                            Boolean isEndTag;
                            if (IsAsciiLetter(remaining[1]))
                            {
                                fused = 2;
                                isEndTag = false;
                            }
                            else if (remaining[1] == (Byte)'/' && remaining.Length >= 3 && IsAsciiLetter(remaining[2]))
                            {
                                fused = 3;
                                isEndTag = true;
                            }
                            else
                            {
                                goto PerByteStateMachine;
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
                    }
                    else if (_state is State.RawText or State.ScriptData && _textUtf8CarryLength == 0)
                    {
                        var consumed = _captureText
                            ? ScanRawTextContent<TMetrics, TTrust, CaptureOn>(
                                utf8[index..],
                                sourceBase + index,
                                trackSourceRanges,
                                yieldOnRequest
                            )
                            : ScanRawTextContent<TMetrics, TTrust, CaptureOff>(
                                utf8[index..],
                                sourceBase + index,
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
                    // Comment bodies are 0.19% of corpus bytes (1,110 comments across 28.6 MB) and
                    // Plaintext never occurs at all, so both scan out-of-line: their only claim on
                    // this method would be its instruction footprint.
                    else if (_state == State.Comment)
                    {
                        var consumed = ScanCommentContent<TMetrics, TTrust>(utf8[index..]);
                        if (consumed > 0)
                        {
                            index += consumed;
                            continue;
                        }
                    }
                    else if (_state == State.Plaintext && _textUtf8CarryLength == 0)
                    {
                        var consumed = ScanPlaintextContent<TMetrics, TTrust>(utf8[index..], sourceBase + index);
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
                }
                
                PerByteStateMachine:
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
                        if (RawTextEnabled && IsRawTextInputState(_state))
                            EmitRawCurrentByte(value, CurrentRawTextType());
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
                _startTagSourceRangeSink!.ObserveNormalizedUtf8End(sourceBase, utf8[..index], RewritePublishableOffset);
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
                EmitRawText(_normalizedBytesConsumed - 1, "<"u8, Utf8HtmlTextType.Data);
                break;
            case State.EndTagOpen:
                EmitText("</"u8);
                EmitRawText(_normalizedBytesConsumed - 2, "</"u8, Utf8HtmlTextType.Data);
                break;
            case State.CharacterReference:
                ResolveCharacterReference();
                break;
            case State.CDataSectionBracket:
                EmitCDataText("]"u8);
                EmitRawText(_normalizedBytesConsumed - 1, "]"u8, Utf8HtmlTextType.CDataSection);
                break;
            case State.CDataSectionEnd:
                EmitCDataText("]]"u8);
                EmitRawText(_normalizedBytesConsumed - 2, "]]"u8, Utf8HtmlTextType.CDataSection);
                break;
            case State.RawLessThan:
            case State.RawEndTagOpen:
            case State.RawEndTagName:
                EmitText(_candidate.WrittenSpan);
                EmitRawText(
                    _normalizedBytesConsumed - _candidate.WrittenCount,
                    _candidate.WrittenSpan,
                    CurrentRawTextType()
                );
                break;
            case State.ScriptLessThan:
            case State.ScriptEscapedLessThan:
                EmitText("<"u8);
                EmitRawText(_normalizedBytesConsumed - 1, "<"u8, Utf8HtmlTextType.ScriptData);
                break;
            case State.ScriptEndTagName:
            case State.ScriptEscapedEndTagName:
                EmitText(_candidate.WrittenSpan);
                EmitRawText(
                    _normalizedBytesConsumed - _candidate.WrittenCount,
                    _candidate.WrittenSpan,
                    Utf8HtmlTextType.ScriptData
                );
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

        EndRawText(_normalizedBytesConsumed);
        _sink.EndOfFile();
        _completed = true;
    }

    private void RefreshCapture() => _captureText = (_sink.Capture & Utf8HtmlTokenCapture.Text) != 0;

    /// <summary>
    /// True while a raw-text sink is attached. Callers test this before doing any work that only a
    /// raw-text consumer needs - classifying the text type, materializing a single-byte span - so a
    /// parse with no raw-text sink pays one field test per site and nothing else.
    /// </summary>
    private Boolean RawTextEnabled => (_streamingFlags & RawTextEnabledFlag) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EmitRawCurrentByte(Byte value, Utf8HtmlTextType textType)
    {
        if (RawTextEnabled)
            EmitRawCurrentByteCore(value, textType);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EmitRawCurrentByteCore(Byte value, Utf8HtmlTextType textType)
    {
        Span<Byte> source = stackalloc Byte[1];
        source[0] = _pendingCarriageReturn ? (Byte)'\r' : value;
        EmitRawTextCore(_currentSourceOffset - 1, source, textType);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EmitRawText(Int64 sourceStart, ReadOnlySpan<Byte> utf8, Utf8HtmlTextType textType)
    {
        if (RawTextEnabled && !utf8.IsEmpty)
            EmitRawTextCore(sourceStart, utf8, textType);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EmitRawTextCore(Int64 sourceStart, ReadOnlySpan<Byte> utf8, Utf8HtmlTextType textType)
    {
        if (utf8.IsEmpty || _streamingCommentSink is not IUtf8HtmlRawTextSink { WantsRawText: true } rawTextSink)
            return;
        rawTextSink.RawText(sourceStart, utf8, textType, isLastInTextNode: false);
        _streamingFlags = (Byte)(
            (_streamingFlags & (CommentStartedFlag | CaptureCommentFlag | RawTextEnabledFlag))
            | RawTextNodeOpenFlag
            | ((Byte)textType << RawTextTypeShift)
        );
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EndRawText(Int64 sourceOffset)
    {
        if ((_streamingFlags & RawTextNodeOpenFlag) != 0)
            EndRawTextCore(sourceOffset);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EndRawTextCore(Int64 sourceOffset)
    {
        ((IUtf8HtmlRawTextSink)_streamingCommentSink!).RawText(
            sourceOffset,
            [],
            (Utf8HtmlTextType)((_streamingFlags & RawTextTypeMask) >> RawTextTypeShift),
            isLastInTextNode: true
        );
        _streamingFlags &= CommentStartedFlag | CaptureCommentFlag | RawTextEnabledFlag;
    }

    private Utf8HtmlTextType CurrentRawTextType() =>
        _state switch
        {
            State.Plaintext => Utf8HtmlTextType.PlainText,
            >= State.ScriptData and <= State.ScriptDoubleEscapeEnd => Utf8HtmlTextType.ScriptData,
            >= State.CDataSection and <= State.CDataSectionEnd => Utf8HtmlTextType.CDataSection,
            >= State.RawText and <= State.RawEndTagName => IsRcData()
                ? Utf8HtmlTextType.RcData
                : Utf8HtmlTextType.RawText,
            State.CharacterReference when _returnState == State.RawText => IsRcData()
                ? Utf8HtmlTextType.RcData
                : Utf8HtmlTextType.RawText,
            _ => Utf8HtmlTextType.Data,
        };

    private static Boolean IsRawTextInputState(State state) =>
        state
            is State.Data
                or State.Plaintext
                or State.RawText
                or State.CDataSection
                or >= State.ScriptData
                and <= State.ScriptDoubleEscapeEnd;

    private Boolean StreamingCommentStarted
    {
        get => (_streamingFlags & CommentStartedFlag) != 0;
        set =>
            _streamingFlags = value
                ? (Byte)(_streamingFlags | CommentStartedFlag)
                : (Byte)(_streamingFlags & ~CommentStartedFlag);
    }

    private Boolean CapturesStreamingComment
    {
        get => (_streamingFlags & CaptureCommentFlag) != 0;
        set =>
            _streamingFlags = value
                ? (Byte)(_streamingFlags | CaptureCommentFlag)
                : (Byte)(_streamingFlags & ~CaptureCommentFlag);
    }

    private const Byte CommentStartedFlag = 0x01;
    private const Byte CaptureCommentFlag = 0x02;
    private const Byte RawTextEnabledFlag = 0x80;
    private const Byte RawTextNodeOpenFlag = 0x40;
    private const Byte RawTextTypeMask = 0x1C;
    private const Int32 RawTextTypeShift = 2;

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

    // Measured 2026-08-09: rewriting this as an explicit 64-bit mask test moved nothing
    // (news.google -0.13%, linkedin +0.16%, stackoverflow -1.79% retired instructions, inside the
    // run-to-run spread). The JIT already lowers a constant or-pattern over a byte to a bit test,
    // so there is no chain to shorten here. Left as the readable form on purpose.
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
