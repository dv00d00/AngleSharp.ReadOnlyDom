using System.Buffers;
using System.Runtime.CompilerServices;

namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal partial class Utf8HtmlTokenizer<TResourceLimits>
    where TResourceLimits : struct, IResourceLimitPolicy
{
    private static readonly SearchValues<Byte> DataTextTerminators = SearchValues.Create("<&\0\r"u8);
    private static readonly SearchValues<Byte> PlaintextTerminators = SearchValues.Create("\0\r"u8);
    private static readonly SearchValues<Byte> DataTextArbitraryAllowed = CreateArbitraryAllowed("<&\0\r"u8);
    private static readonly SearchValues<Byte> PlaintextArbitraryAllowed = CreateArbitraryAllowed("\0\r"u8);

    /// <summary>
    /// Bulk-scans <c>&lt;plaintext&gt;</c> content, returning the bytes consumed. The element is a
    /// parse error that terminates only at end of input, and occurs zero times across the corpus,
    /// so it scans out of line to keep the data-state arm free of a per-run state test.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Int32 ScanPlaintextContent<TMetrics, TTrust>(ReadOnlySpan<Byte> utf8, Int64 sourceOffset)
        where TMetrics : struct, IStateMetricsPolicy
        where TTrust : struct, IInputTrustPolicy
    {
        var run = _captureText
            ? IndexOfCaptureStop<TTrust>(utf8, PlaintextTerminators, PlaintextArbitraryAllowed)
            : utf8.Length;
        if (run < 0)
        {
            run = utf8.Length;
        }
        if (run > 0)
        {
            RecordState<TMetrics>((Int32)State.Plaintext, run);
            if (_captureText)
            {
                EmitText(utf8[..run]);
                if (RawTextEnabled)
                {
                    EmitRawText(sourceOffset, utf8[..run], CurrentRawTextType());
                }
            }
        }
        return run;
    }

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
                    EmitRawCurrentByte(value, Utf8HtmlTextType.Data);
                    BeginCharacterReference(State.Data);
                }
                else if (_captureText)
                {
                    EmitRawCurrentByte(value, Utf8HtmlTextType.Data);
                    EmitByte(value);
                }

                break;
            case State.Plaintext:
                EmitRawCurrentByte(value, Utf8HtmlTextType.PlainText);
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
