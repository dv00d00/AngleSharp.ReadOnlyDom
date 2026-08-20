using System.Runtime.CompilerServices;

namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal partial class Utf8HtmlTokenizer<TResourceLimits>
    where TResourceLimits : struct, IResourceLimitPolicy
{
    private void RefreshCapture() => _captureText = (_sink.Capture & Utf8HtmlTokenCapture.Text) != 0;

    /// <summary>
    /// True while a raw-text sink is attached. Callers test this before doing any work that only a
    /// raw-text consumer needs - classifying the text type, materializing a single-byte span - so a
    /// parse with no raw-text sink pays one field test per site and nothing else.
    /// </summary>
    private Boolean RawTextEnabled => (_streamingFlags & RawTextEnabledFlag) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EmitRawCurrentByte(Byte value, Utf8HtmlTextType textType)
    {
        if (RawTextEnabled)
            EmitRawCurrentByteCore(value, textType);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EmitRawCurrentByteCore(Byte value, Utf8HtmlTextType textType)
    {
        Span<Byte> source = stackalloc Byte[1];
        source[0] = _pendingCarriageReturn ? (Byte)'\r' : value;
        EmitRawTextCore(_currentSourceOffset - 1, source, textType);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EmitRawText(Int64 sourceStart, ReadOnlySpan<Byte> utf8, Utf8HtmlTextType textType)
    {
        if (RawTextEnabled && !utf8.IsEmpty)
            EmitRawTextCore(sourceStart, utf8, textType);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EmitRawTextCore(Int64 sourceStart, ReadOnlySpan<Byte> utf8, Utf8HtmlTextType textType)
    {
        if (utf8.IsEmpty || _streamingCommentSink is not IUtf8HtmlRawTextSink { WantsRawText: true } rawTextSink)
            return;
        rawTextSink.RawText(sourceStart, utf8, textType, isLastInTextNode: false);
        _streamingFlags = (Byte)(
            (_streamingFlags & (CommentStartedFlag | CaptureCommentFlag | RawTextEnabledFlag))
            | RawTextNodeOpenFlag
            | ((Byte)textType << RawTextTypeShift)
        );
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EndRawText(Int64 sourceOffset)
    {
        if ((_streamingFlags & RawTextNodeOpenFlag) != 0)
            EndRawTextCore(sourceOffset);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EndRawTextCore(Int64 sourceOffset)
    {
        ((IUtf8HtmlRawTextSink)_streamingCommentSink!).RawText(
            sourceOffset,
            [],
            (Utf8HtmlTextType)((_streamingFlags & RawTextTypeMask) >> RawTextTypeShift),
            isLastInTextNode: true
        );
        _streamingFlags &= CommentStartedFlag | CaptureCommentFlag | RawTextEnabledFlag;
    }

    private Utf8HtmlTextType CurrentRawTextType() =>
        _state switch
        {
            State.Plaintext => Utf8HtmlTextType.PlainText,
            >= State.ScriptData and <= State.ScriptDoubleEscapeEnd => Utf8HtmlTextType.ScriptData,
            >= State.CDataSection and <= State.CDataSectionEnd => Utf8HtmlTextType.CDataSection,
            >= State.RawText and <= State.RawEndTagName => IsRcData()
                ? Utf8HtmlTextType.RcData
                : Utf8HtmlTextType.RawText,
            State.CharacterReference when _returnState == State.RawText => IsRcData()
                ? Utf8HtmlTextType.RcData
                : Utf8HtmlTextType.RawText,
            _ => Utf8HtmlTextType.Data,
        };

    private static Boolean IsRawTextInputState(State state) =>
        state
            is State.Data
                or State.Plaintext
                or State.RawText
                or State.CDataSection
                or >= State.ScriptData
                and <= State.ScriptDoubleEscapeEnd;

    private Boolean StreamingCommentStarted
    {
        get => (_streamingFlags & CommentStartedFlag) != 0;
        set =>
            _streamingFlags = value
                ? (Byte)(_streamingFlags | CommentStartedFlag)
                : (Byte)(_streamingFlags & ~CommentStartedFlag);
    }

    private Boolean CapturesStreamingComment
    {
        get => (_streamingFlags & CaptureCommentFlag) != 0;
        set =>
            _streamingFlags = value
                ? (Byte)(_streamingFlags | CaptureCommentFlag)
                : (Byte)(_streamingFlags & ~CaptureCommentFlag);
    }

    private const Byte CommentStartedFlag = 0x01;
    private const Byte CaptureCommentFlag = 0x02;
    private const Byte RawTextEnabledFlag = 0x80;
    private const Byte RawTextNodeOpenFlag = 0x40;
    private const Byte RawTextTypeMask = 0x1C;
    private const Int32 RawTextTypeShift = 2;

    private void EmitText(ReadOnlySpan<Byte> utf8)
    {
        if (_captureText)
        {
            _sink.Text(utf8);
        }
    }

    private void EmitReplacementCharacter() => EmitText("\uFFFD"u8);

    private void EmitCDataText(ReadOnlySpan<Byte> utf8)
    {
        if (!SkipCDATA)
        {
            EmitText(utf8);
        }
    }

    private void EmitCDataByte(Byte value)
    {
        if (!SkipCDATA)
        {
            EmitByte(value);
        }
    }

    private void EmitByte(Byte value)
    {
        if (!_captureText)
        {
            return;
        }

        if (_textUtf8CarryLength != 0)
        {
            _textUtf8Carry |= (UInt32)value << (_textUtf8CarryLength++ * 8);
            if (_textUtf8CarryLength == _textUtf8ExpectedLength)
            {
                Span<Byte> scalar = stackalloc Byte[4];
                for (var index = 0; index < _textUtf8CarryLength; index++)
                {
                    scalar[index] = (Byte)(_textUtf8Carry >> (index * 8));
                }

                EmitText(scalar[.._textUtf8CarryLength]);
                _textUtf8Carry = 0;
                _textUtf8CarryLength = 0;
                _textUtf8ExpectedLength = 0;
            }

            return;
        }

        if (value >= 0x80)
        {
            _textUtf8Carry = value;
            _textUtf8CarryLength = 1;
            _textUtf8ExpectedLength = Utf8SequenceLength(value);
            return;
        }

        Span<Byte> single = stackalloc Byte[1];
        single[0] = value;
        EmitText(single);
    }

    private void Reconsume(ref Boolean reconsume, State state)
    {
        _state = state;
        reconsume = true;
        _reconsumes++;
    }

    private void Append(Utf8TokenBuffer buffer, Byte value)
    {
        if (TResourceLimits.Enabled)
        {
            EnsureBufferedTokenCapacity(1);
        }

        buffer.Append(value);
        if (TResourceLimits.Enabled)
        {
            ObserveBufferAppend(1);
        }
    }

    private void Append(Utf8TokenBuffer buffer, ReadOnlySpan<Byte> value)
    {
        if (TResourceLimits.Enabled)
        {
            EnsureBufferedTokenCapacity(value.Length);
        }

        buffer.Append(value);
        if (TResourceLimits.Enabled)
        {
            ObserveBufferAppend(value.Length);
        }
    }

    private void ObserveBufferAppend(Int32 count)
    {
        _bufferedTokenBytes += count;
        if (_bufferedTokenBytes > _maximumBufferedTokenBytes)
        {
            _maximumBufferedTokenBytes = (Int32)Math.Min(_bufferedTokenBytes, Int32.MaxValue);
        }
    }

    private void EnsureBufferedTokenCapacity(Int32 additional)
    {
        var observed = SaturatingAdd(_bufferedTokenBytes, additional);
        if (observed > _maximumBufferedTokenBytesAllowed)
        {
            ThrowLimitExceeded(HtmlStreamingLimit.BufferedTokenBytes, _maximumBufferedTokenBytesAllowed, observed);
        }
    }

    private static ReadOnlySpan<Byte> WrittenSpan(Utf8TokenBuffer? buffer) =>
        buffer is null ? ReadOnlySpan<Byte>.Empty : buffer.WrittenSpan;

    private void Clear(Utf8TokenBuffer? buffer)
    {
        if (buffer is null)
        {
            return;
        }

        if (TResourceLimits.Enabled)
        {
            _bufferedTokenBytes -= buffer.WrittenCount;
        }

        buffer.ResetWrittenCount();
    }

    private static Int64 SaturatingAdd(Int64 value, Int32 additional) =>
        value > Int64.MaxValue - additional ? Int64.MaxValue : value + additional;

    private static void ThrowLimitExceeded(HtmlStreamingLimit limit, Int64 allowed, Int64 observed) =>
        throw new HtmlStreamingLimitExceededException(limit, allowed, observed);

    private static Int32 Utf8SequenceLength(Byte lead) =>
        lead switch
        {
            < 0x80 => 1,
            < 0xE0 => 2,
            < 0xF0 => 3,
            < 0xF8 => 4,
            _ => 1,
        };

    private void AppendTagName(Byte value) => Append(_name, value);

    private void AppendTagName(ReadOnlySpan<Byte> value) => Append(_name, value);

    private void AppendTagNameReplacedNull(Byte value)
    {
        if (value == 0)
        {
            AppendTagName("\uFFFD"u8);
        }
        else
        {
            AppendTagName(value);
        }
    }

    private Utf8HtmlName CurrentTagName() => new(_name.WrittenSpan, ref _tagNameIdentityCache);

    private Utf8HtmlName CurrentAttributeName() => new(_attributeName.WrittenSpan, ref _attributeNameIdentityCache);

    private Boolean IsAttributeReturnState() => _returnState is not State.Data and not State.RawText;

    private static Boolean StartsWithAsciiIgnoreCase(ReadOnlySpan<Byte> expected, ReadOnlySpan<Byte> candidate)
    {
        if (candidate.Length > expected.Length)
        {
            return false;
        }

        for (var i = 0; i < candidate.Length; i++)
        {
            if (AsciiLower(expected[i]) != AsciiLower(candidate[i]))
            {
                return false;
            }
        }

        return true;
    }

    // Measured 2026-08-09: rewriting this as an explicit 64-bit mask test moved nothing
    // (news.google -0.13%, linkedin +0.16%, stackoverflow -1.79% retired instructions, inside the
    // run-to-run spread). The JIT already lowers a constant or-pattern over a byte to a bit test,
    // so there is no chain to shorten here. Left as the readable form on purpose.
    private static Boolean IsSpace(Byte value) => value is 0x09 or 0x0A or 0x0C or 0x0D or 0x20;

    private static Boolean IsAsciiLetter(Byte value) =>
        (UInt32)(value - 'A') <= 'Z' - 'A' || (UInt32)(value - 'a') <= 'z' - 'a';

    private static Boolean IsAsciiAlphaNumeric(Byte value) => IsAsciiLetter(value) || (UInt32)(value - '0') <= 9;

    private static Boolean IsTagDelimiter(Byte value) => value is (Byte)'>' or (Byte)'/' || IsSpace(value);

    private static Byte AsciiLower(Byte value) => Utf8NameHash.ToLowerAscii(value);

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The tokenizer is already complete.");
        }
    }
}
