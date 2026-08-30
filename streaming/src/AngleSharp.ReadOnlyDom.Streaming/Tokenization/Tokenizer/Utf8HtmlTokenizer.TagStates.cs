namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal partial class Utf8HtmlTokenizer<TResourceLimits>
    where TResourceLimits : struct, IResourceLimitPolicy
{
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
                    EndRawText(_lastLessThanSourceOffset);
                    Clear(_candidate);
                    _state = State.MarkupDeclaration;
                }
                else if (value == (Byte)'?' && IsSupportingProcessingInstructions)
                {
                    EndRawText(_lastLessThanSourceOffset);
                    Clear(_candidate);
                    if (!SkipProcessingInstructions)
                    {
                        Append(_candidate, value);
                    }
                    _state = State.ProcessingInstruction;
                }
                else if (value == (Byte)'?')
                {
                    EndRawText(_lastLessThanSourceOffset);
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
                    EmitRawText(_currentSourceOffset - 2, "<"u8, Utf8HtmlTextType.Data);
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
                    EndRawText(_lastLessThanSourceOffset);
                    _state = State.Data;
                }
                else
                {
                    EndRawText(_lastLessThanSourceOffset);
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
}
