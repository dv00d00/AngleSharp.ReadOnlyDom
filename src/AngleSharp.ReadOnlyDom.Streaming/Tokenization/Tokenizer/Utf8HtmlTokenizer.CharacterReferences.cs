namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal partial class Utf8HtmlTokenizerCore
{
    private void ProcessCharacterReference(Byte value, ref Boolean reconsume)
    {
        var source = _candidate.WrittenSpan;
        if (!source.IsEmpty && source[0] == (Byte)'#')
        {
            if (source.Length == 1 && value is (Byte)'x' or (Byte)'X')
            {
                EmitRawCharacterReferenceByte(value);
                Append(_candidate, value);
                return;
            }

            if (value == (Byte)';')
            {
                EmitRawCharacterReferenceByte(value);
                if (!_numericReferenceOverflow)
                {
                    Append(_candidate, value);
                }

                ResolveCharacterReference();
                _state = _returnState;
                return;
            }

            var isHex = source.Length > 1 && source[1] is (Byte)'x' or (Byte)'X';
            var isDigit = isHex
                ? (UInt32)(value - '0') <= 9 || (UInt32)(AsciiLower(value) - 'a') <= 5
                : (UInt32)(value - '0') <= 9;
            if (isDigit)
            {
                EmitRawCharacterReferenceByte(value);
                var digit = (UInt32)(value - '0') <= 9 ? (UInt32)(value - '0') : (UInt32)(AsciiLower(value) - 'a' + 10);
                var radix = isHex ? 16u : 10u;
                _numericReferenceHasDigits = true;
                if (!_numericReferenceOverflow && _numericReferenceValue <= (0x10FFFFu - digit) / radix)
                {
                    _numericReferenceValue = _numericReferenceValue * radix + digit;
                }
                else
                {
                    _numericReferenceOverflow = true;
                }

                if (_candidate.WrittenCount < 32)
                {
                    Append(_candidate, value);
                }

                return;
            }

            ResolveCharacterReference(value);
            Reconsume(ref reconsume, _returnState);
            return;
        }

        if (value == (Byte)';')
        {
            EmitRawCharacterReferenceByte(value);
            Append(_candidate, value);
            ResolveCharacterReference();
            _state = _returnState;
            return;
        }
        var length = _candidate.WrittenCount;
        if (
            length < 32
            && (
                IsAsciiAlphaNumeric(value)
                || (length == 0 && value == (Byte)'#')
                || (length == 1 && _candidate.WrittenSpan[0] == (Byte)'#' && value is (Byte)'x' or (Byte)'X')
            )
        )
        {
            EmitRawCharacterReferenceByte(value);
            Append(_candidate, value);
            return;
        }
        ResolveCharacterReference(value);
        Reconsume(ref reconsume, _returnState);
    }

    private void EmitRawCharacterReferenceByte(Byte value)
    {
        if (_returnState is State.Data or State.RawText)
            EmitRawCurrentByte(value, CurrentRawTextType());
    }

    private void ResolveCharacterReference(Byte? nextInput = null)
    {
        var source = _candidate.WrittenSpan;
        Span<Byte> replacement = stackalloc Byte[8];
        var replacementLength = 0;
        if (!source.IsEmpty && source[0] == (Byte)'#' && _numericReferenceHasDigits)
        {
            var scalar = (Int32)_numericReferenceValue;
            if (_numericReferenceOverflow)
            {
                replacementLength = WriteReplacementCharacter(replacement);
            }
            else
            {
                var mappedScalar = Utf8HtmlEntityDecoder.GetSymbolCodeFromTable(scalar);
                scalar = mappedScalar < 0 ? scalar : mappedScalar;
                replacementLength = Utf8HtmlEntityDecoder.IsInvalidNumber(scalar)
                    ? WriteReplacementCharacter(replacement)
                    : WriteScalarUtf8(scalar, replacement);
            }
        }
        else if (!source.IsEmpty)
        {
            var entityLength = Utf8HtmlEntityDecoder.WriteLongestSymbolUtf8(source, replacement, out var matchedLength);
            if (entityLength != 0)
            {
                var missingSemicolon = source[matchedLength - 1] != (Byte)';';
                if (
                    missingSemicolon
                    && IsAttributeReturnState()
                    && (
                        (
                            matchedLength < source.Length
                            && (source[matchedLength] == '=' || IsAsciiAlphaNumeric(source[matchedLength]))
                        )
                        || (
                            matchedLength == source.Length
                            && nextInput is Byte next
                            && (next == '=' || IsAsciiAlphaNumeric(next))
                        )
                    )
                )
                {
                    entityLength = 0;
                }
            }
            if (entityLength != 0)
            {
                AppendCharacterReferenceResult(replacement[..entityLength]);
                AppendCharacterReferenceResult(source[matchedLength..]);
                Clear(_candidate);
                return;
            }
        }

        if (replacementLength != 0)
        {
            AppendCharacterReferenceResult(replacement.Slice(0, replacementLength));
        }
        else
        {
            AppendCharacterReferenceResult("&"u8);
            AppendCharacterReferenceResult(source);
        }
        Clear(_candidate);
    }

    private static Int32 WriteReplacementCharacter(Span<Byte> destination) =>
        Utf8AttributeValueDecoder.WriteReplacementCharacter(destination);

    private static Int32 WriteScalarUtf8(Int32 scalar, Span<Byte> destination) =>
        Utf8AttributeValueDecoder.WriteScalarUtf8(scalar, destination);

    private void AppendCharacterReferenceResult(ReadOnlySpan<Byte> utf8)
    {
        if (_returnState is State.Data or State.RawText)
        {
            EmitText(utf8);
        }
        else if (_attributeCapture == AttributeCapture.Capture)
        {
            Append(AttributeValue, utf8);
        }
    }

    private void BeginCharacterReference(State returnState)
    {
        Clear(_candidate);
        _numericReferenceOverflow = false;
        _numericReferenceHasDigits = false;
        _numericReferenceValue = 0;
        _returnState = returnState;
        _state = State.CharacterReference;
    }
}
