namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal partial class Utf8HtmlTokenizer<TResourceLimits>
    where TResourceLimits : struct, IResourceLimitPolicy
{
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

        try
        {
            while (index < utf8.Length)
            {
                if (!_pendingCarriageReturn)
                {
                    if (IsTagTailState(_state) && (_isEndTag || _startTagEmitted))
                    {
                        var sourceOffset = trackSourceRanges ? sourceBase + index : 0;
                        var consumed =
                            !_isEndTag && _captureStartTagAttributes
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
                        var stop = IndexOfTagNameStop<TTrust>(remaining);
                        var run = stop < 0 ? remaining.Length : stop;

                        if (run > 0)
                        {
                            // _state is TagName by the test above; naming the constant lets it fold
                            // instead of reloading the field in the hot path.
                            RecordState<TMetrics>((Int32)State.TagName, run);
                            AppendTagName(remaining[..run]);
                            index += run;
                            if (stop < 0)
                            {
                                // The name continues past this span; nothing to fuse.
                                continue;
                            }
                        }

                        var stopByte = remaining[run];
                        // Tag-name stop fusion: the byte that ended the name is in-span and its whole
                        // effect here is one of three transitions, so take them instead of a per-byte
                        // round-trip. Not the full terminator set - '\0' becomes a replacement
                        // character, '\r' starts CR normalization, non-ASCII must bounce to the caller.
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
                    else if (_state == State.Data && _textUtf8CarryLength == 0)
                    {
                        var remaining = utf8.Slice(index);
                        Int32 run;

                        if (remaining[0] == (Byte)'<')
                        {
                            run = 0;
                        }
                        else if (!_captureText)
                        {
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
}
