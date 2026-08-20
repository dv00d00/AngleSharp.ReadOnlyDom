namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal partial class Utf8HtmlTokenizer<TResourceLimits>
    where TResourceLimits : struct, IResourceLimitPolicy
{
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
                    EmitRawText(_currentSourceOffset - 2, "<!"u8, Utf8HtmlTextType.ScriptData);
                    _state = State.ScriptEscapeStart;
                }
                else
                {
                    EmitText("<"u8);
                    EmitRawText(_currentSourceOffset - 2, "<"u8, Utf8HtmlTextType.ScriptData);
                    Reconsume(ref reconsume, State.ScriptData);
                }
                break;
            case State.ScriptEscapeStart:
                if (value == '-')
                {
                    EmitRawCurrentByte(value, Utf8HtmlTextType.ScriptData);
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
                    EmitRawCurrentByte(value, Utf8HtmlTextType.ScriptData);
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
                    EmitRawCurrentByte(value, Utf8HtmlTextType.ScriptData);
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
                    EmitRawCurrentByte(value, Utf8HtmlTextType.ScriptData);
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
                    EmitRawCurrentByte(value, Utf8HtmlTextType.ScriptData);
                    EmitByte(value);
                }
                else if (value == '<')
                {
                    _state = State.ScriptEscapedLessThan;
                }
                else if (value == '>')
                {
                    EmitRawCurrentByte(value, Utf8HtmlTextType.ScriptData);
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
                    EmitRawText(_currentSourceOffset - 2, "<"u8, Utf8HtmlTextType.ScriptData);
                    Clear(_candidate);
                    Append(_candidate, AsciiLower(value));
                    EmitRawCurrentByte(value, Utf8HtmlTextType.ScriptData);
                    EmitByte(value);
                    _state = State.ScriptDoubleEscapeStart;
                }
                else
                {
                    EmitText("<"u8);
                    EmitRawText(_currentSourceOffset - 2, "<"u8, Utf8HtmlTextType.ScriptData);
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
                    EmitRawCurrentByte(value, Utf8HtmlTextType.ScriptData);
                    EmitByte(value);
                }
                else if (IsTagDelimiter(value))
                {
                    var script = _candidate.WrittenSpan.SequenceEqual("script"u8);
                    Clear(_candidate);
                    EmitRawCurrentByte(value, Utf8HtmlTextType.ScriptData);
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
                    EmitRawCurrentByte(value, Utf8HtmlTextType.ScriptData);
                    EmitByte(value);
                    _state = State.ScriptDoubleEscapedDash;
                }
                else if (value == '<')
                {
                    EmitRawCurrentByte(value, Utf8HtmlTextType.ScriptData);
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
                    EmitRawCurrentByte(value, Utf8HtmlTextType.ScriptData);
                    EmitByte(value);
                    _state = State.ScriptDoubleEscapedDashDash;
                }
                else if (value == '<')
                {
                    EmitRawCurrentByte(value, Utf8HtmlTextType.ScriptData);
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
                    EmitRawCurrentByte(value, Utf8HtmlTextType.ScriptData);
                    EmitByte(value);
                }
                else if (value == '<')
                {
                    EmitRawCurrentByte(value, Utf8HtmlTextType.ScriptData);
                    EmitByte(value);
                    _state = State.ScriptDoubleEscapedLessThan;
                }
                else if (value == '>')
                {
                    EmitRawCurrentByte(value, Utf8HtmlTextType.ScriptData);
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
                    EmitRawCurrentByte(value, Utf8HtmlTextType.ScriptData);
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
                    EmitRawCurrentByte(value, Utf8HtmlTextType.ScriptData);
                    EmitByte(value);
                }
                else if (IsTagDelimiter(value))
                {
                    var script = _candidate.WrittenSpan.SequenceEqual("script"u8);
                    Clear(_candidate);
                    EmitRawCurrentByte(value, Utf8HtmlTextType.ScriptData);
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
            EmitRawText(_currentSourceOffset, remaining[..run], Utf8HtmlTextType.ScriptData);
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
            EndRawText(_currentSourceOffset - 1 - candidate.Length);
            if (_startTagSourceRangeSink is not null)
                _currentTagSourceOffset = _currentSourceOffset - candidate.Length - 1;
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
        EmitRawText(
            _currentSourceOffset - 1 - _candidate.WrittenCount,
            _candidate.WrittenSpan,
            Utf8HtmlTextType.ScriptData
        );
        Clear(_candidate);
        Reconsume(ref reconsume, fallback);
    }

    private void EmitScriptByte(Byte value)
    {
        EmitRawCurrentByte(value, Utf8HtmlTextType.ScriptData);
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
}
