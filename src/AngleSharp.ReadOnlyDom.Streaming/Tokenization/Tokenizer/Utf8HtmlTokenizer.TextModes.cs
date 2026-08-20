using System.Buffers;

namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal partial class Utf8HtmlTokenizer<TResourceLimits>
    where TResourceLimits : struct, IResourceLimitPolicy
{
    private static readonly SearchValues<Byte> RawTextTerminators = SearchValues.Create("<\0\r"u8);
    private static readonly SearchValues<Byte> EscapedScriptTextTerminators = SearchValues.Create("<-\0\r"u8);
    private static readonly SearchValues<Byte> RawTextArbitraryAllowed = CreateArbitraryAllowed("<\0\r"u8);
    private static readonly SearchValues<Byte> EscapedScriptTextArbitraryAllowed = CreateArbitraryAllowed("<-\0\r"u8);

    private void ProcessRawTextState(Byte value, ref Boolean reconsume)
    {
        switch (_state)
        {
            case State.RawText:
                if (value == (Byte)'<')
                {
                    Clear(_candidate);
                    Append(_candidate, value);
                    _state = State.RawLessThan;
                }
                else if (value == (Byte)'&' && _captureText && IsRcData() && !IsNotConsumingCharacterReferences)
                {
                    EmitRawCurrentByte(value, Utf8HtmlTextType.RcData);
                    BeginCharacterReference(State.RawText);
                }
                else if (value == 0)
                {
                    EmitRawCurrentByte(value, IsRcData() ? Utf8HtmlTextType.RcData : Utf8HtmlTextType.RawText);
                    EmitReplacementCharacter();
                }
                else
                {
                    EmitRawCurrentByte(value, IsRcData() ? Utf8HtmlTextType.RcData : Utf8HtmlTextType.RawText);
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
                    EmitRawText(
                        _currentSourceOffset - 1 - _candidate.WrittenCount,
                        _candidate.WrittenSpan,
                        IsRcData() ? Utf8HtmlTextType.RcData : Utf8HtmlTextType.RawText
                    );
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
                    EndRawText(_currentSourceOffset - 1 - _candidate.WrittenCount);
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
                    EmitRawText(
                        _currentSourceOffset - 1 - _candidate.WrittenCount,
                        _candidate.WrittenSpan,
                        IsRcData() ? Utf8HtmlTextType.RcData : Utf8HtmlTextType.RawText
                    );
                    Clear(_candidate);
                    Reconsume(ref reconsume, State.RawText);
                }
                break;
            default:
                throw new InvalidOperationException($"Unexpected {nameof(State)} value: {_state}");
        }
    }

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
}
