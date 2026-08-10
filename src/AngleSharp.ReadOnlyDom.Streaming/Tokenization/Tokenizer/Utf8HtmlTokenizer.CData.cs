namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal partial class Utf8HtmlTokenizerCore
{
    private void ProcessCDataState(Byte value, ref Boolean reconsume)
    {
        switch (_state)
        {
            case State.CDataSection:
                if (value == (Byte)']')
                {
                    _state = State.CDataSectionBracket;
                }
                else
                {
                    EmitRawCurrentByte(value, Utf8HtmlTextType.CDataSection);
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
                    EmitRawText(_currentSourceOffset - 2, "]"u8, Utf8HtmlTextType.CDataSection);
                    Reconsume(ref reconsume, State.CDataSection);
                }
                break;
            case State.CDataSectionEnd:
                if (value == (Byte)']')
                {
                    EmitCDataText("]"u8);
                    EmitRawText(_currentSourceOffset - 3, "]"u8, Utf8HtmlTextType.CDataSection);
                }
                else if (value == (Byte)'>')
                {
                    EndRawText(_currentSourceOffset - 3);
                    _state = State.Data;
                }
                else
                {
                    EmitCDataText("]]"u8);
                    EmitRawText(_currentSourceOffset - 3, "]]"u8, Utf8HtmlTextType.CDataSection);
                    Reconsume(ref reconsume, State.CDataSection);
                }
                break;
            default:
                throw new InvalidOperationException($"Unexpected {nameof(State)} value: {_state}");
        }
    }
}
