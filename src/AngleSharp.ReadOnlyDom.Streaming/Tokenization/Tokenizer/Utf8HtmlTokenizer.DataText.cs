using System.Buffers;

namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal partial class Utf8HtmlTokenizer<TResourceLimits>
    where TResourceLimits : struct, IResourceLimitPolicy
{
    private static readonly SearchValues<Byte> DataTextTerminators = SearchValues.Create("<&\0\r"u8);
    private static readonly SearchValues<Byte> PlaintextTerminators = SearchValues.Create("\0\r"u8);
    private static readonly SearchValues<Byte> DataTextArbitraryAllowed = CreateArbitraryAllowed("<&\0\r"u8);
    private static readonly SearchValues<Byte> PlaintextArbitraryAllowed = CreateArbitraryAllowed("\0\r"u8);

    private void ProcessDataState(Byte value, ref Boolean reconsume)
    {
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
            default:
                throw new InvalidOperationException($"Unexpected {nameof(State)} value: {_state}");
        }
    }
}
