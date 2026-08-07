namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal partial class Utf8HtmlTokenizer<TResourceLimits>
    where TResourceLimits : struct, IResourceLimitPolicy
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
            default:
                throw new InvalidOperationException($"Unexpected {nameof(State)} value: {_state}");
        }
    }
}
