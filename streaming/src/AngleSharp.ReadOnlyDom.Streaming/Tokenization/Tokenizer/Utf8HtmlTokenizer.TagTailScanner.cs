using System.Runtime.CompilerServices;

namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal partial class Utf8HtmlTokenizer<TResourceLimits>
    where TResourceLimits : struct, IResourceLimitPolicy
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
                : IndexOfDiscardedAttributeNameStop(remaining);
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
