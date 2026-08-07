using System.Buffers;
using System.Runtime.CompilerServices;

namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal partial class Utf8HtmlTokenizer<TResourceLimits>
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

    private enum AttributeCapture : byte
    {
        Undecided,
        Capture,
        Discard,
        Duplicate,
    }

    private static readonly SearchValues<Byte> TagNameTerminators = SearchValues.Create("\0\t\n\f\r />"u8);
    private static readonly SearchValues<Byte> TagNameArbitraryAllowed = CreateArbitraryAllowed("\0\t\n\f\r />"u8);
    private static readonly SearchValues<Byte> AttributeNameTerminators = SearchValues.Create("\0\t\n\f\r /=>"u8);
    private static readonly SearchValues<Byte> DiscardedAttributeNameTerminators = SearchValues.Create(
        "\t\n\f\r /=>"u8
    );
    private static readonly SearchValues<Byte> AttributeNameArbitraryAllowed = CreateArbitraryAllowed(
        "\0\t\n\f\r /=>"u8
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
    private static readonly SearchValues<Byte> DoubleQuotedAttributeValueArbitraryAllowed = CreateArbitraryAllowed(
        "\"\0\r"u8
    );
    private static readonly SearchValues<Byte> SingleQuotedAttributeValueArbitraryAllowed = CreateArbitraryAllowed(
        "'\0\r"u8
    );
    private static readonly SearchValues<Byte> UnquotedAttributeValueArbitraryAllowed = CreateArbitraryAllowed(
        "\0>\t\n\f\r "u8
    );

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
        if (_captureStartTagAttributes)
        {
            // One virtual fetch per captured tag; DecideAttributeCapture then pre-filters every
            // attribute of the tag against this snapshot without calling back into the sink.
            _attributeNameFilter = _sink.StartTagAttributeFilter;
        }
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
        // Query-directed pre-filter: the sink published a bloom over the semantic hashes of every
        // attribute name it can still want on this tag (IUtf8HtmlTokenSink.StartTagAttributeFilter).
        // A missed bit proves the sink rejects this name, so the fast path skips Utf8HtmlName
        // materialization, the virtual WantsAttribute call, and duplicate tracking. Skipping the
        // duplicate tracking is safe: suppression is only observable for emitted attributes, and a
        // semantically equal respelling folds to the same hash and therefore the same verdict, so
        // every occurrence of a filter-rejected name is rejected here — a rejected occurrence can
        // never be the first-seen occurrence of an accepted name. Hash false positives merely fall
        // through to the exact path below, which behaves exactly as the unfiltered tokenizer.
        var filter = _attributeNameFilter;
        if (
            filter != UInt64.MaxValue
            && (
                filter
                & Utf8NameHash.AttributeFilterBit(_attributeNameIdentityCache.GetOrCompute(_attributeName.WrittenSpan))
            ) == 0
        )
        {
            _attributeCapture = AttributeCapture.Discard;
            return;
        }
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
    private void ProcessTagState(Byte value, ref Boolean reconsume)
    {
        switch (_state)
        {
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
            default:
                throw new InvalidOperationException($"Unexpected {nameof(State)} value: {_state}");
        }
    }

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
            }
            // CaptureOff still has to consume the byte that starts the discarded name. Leaving
            // it for AttributeName changes its meaning when the byte is also a scanner delimiter
            // (notably '=' after an unexpected solidus on an end tag).
            index++;
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
}
