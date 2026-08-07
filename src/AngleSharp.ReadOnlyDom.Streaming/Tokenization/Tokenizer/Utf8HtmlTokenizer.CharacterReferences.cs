using System.Runtime.CompilerServices;

namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal partial class Utf8HtmlTokenizer<TResourceLimits>
    where TResourceLimits : struct, IResourceLimitPolicy
{
    private void ProcessCharacterReference(Byte value, ref Boolean reconsume)
    {
        var source = _candidate.WrittenSpan;
        if (!source.IsEmpty && source[0] == (Byte)'#')
        {
            if (source.Length == 1 && value is (Byte)'x' or (Byte)'X')
            {
                Append(_candidate, value);
                return;
            }

            if (value == (Byte)';')
            {
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
            Append(_candidate, value);
            return;
        }
        ResolveCharacterReference(value);
        Reconsume(ref reconsume, _returnState);
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

    private static Int32 WriteReplacementCharacter(Span<Byte> destination)
    {
        "\uFFFD"u8.CopyTo(destination);
        return 3;
    }

    private static Int32 WriteScalarUtf8(Int32 scalar, Span<Byte> destination)
    {
        if (scalar <= 0x7F)
        {
            destination[0] = (Byte)scalar;
            return 1;
        }
        if (scalar <= 0x7FF)
        {
            destination[0] = (Byte)(0xC0 | (scalar >> 6));
            destination[1] = (Byte)(0x80 | (scalar & 0x3F));
            return 2;
        }
        if (scalar <= 0xFFFF)
        {
            destination[0] = (Byte)(0xE0 | (scalar >> 12));
            destination[1] = (Byte)(0x80 | ((scalar >> 6) & 0x3F));
            destination[2] = (Byte)(0x80 | (scalar & 0x3F));
            return 3;
        }

        destination[0] = (Byte)(0xF0 | (scalar >> 18));
        destination[1] = (Byte)(0x80 | ((scalar >> 12) & 0x3F));
        destination[2] = (Byte)(0x80 | ((scalar >> 6) & 0x3F));
        destination[3] = (Byte)(0x80 | (scalar & 0x3F));
        return 4;
    }

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

    /// <summary>
    /// Decodes character references over a fully buffered attribute value. References in
    /// attribute values cannot affect tokenization, so the scan buffers their bytes raw and
    /// this pass resolves them once per attribute instead of routing every byte through the
    /// per-byte reference machinery. Mirrors the reference states in attribute context,
    /// including the missing-semicolon suppression rule; the value's real terminator (quote,
    /// space, or '&gt;') is never alphanumeric or '=', so the end of the buffer never
    /// suppresses a match.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DecodeAttributeValueReferences(ReadOnlySpan<Byte> value, Int32 ampersand, Utf8TokenBuffer destination)
    {
        var index = ampersand;
        Append(destination, value[..index]);
        while (true)
        {
            index = DecodeAttributeReference(value, index, destination);
            var run = value[index..].IndexOf((Byte)'&');
            if (run < 0)
            {
                Append(destination, value[index..]);
                return;
            }
            Append(destination, value.Slice(index, run));
            index += run;
        }
    }

    /// <summary>
    /// Decodes the single reference candidate whose '&amp;' sits at <paramref name="index"/>,
    /// appends its result to <paramref name="destination"/>, and returns the index of the
    /// first byte it did not consume.
    /// </summary>
    private Int32 DecodeAttributeReference(ReadOnlySpan<Byte> value, Int32 index, Utf8TokenBuffer destination)
    {
        var position = index + 1;
        if (position < value.Length && value[position] == (Byte)'#')
        {
            return DecodeNumericAttributeReference(value, index, destination);
        }

        // The per-byte machine accumulates at most 32 alphanumeric candidate bytes.
        var start = position;
        while (position < value.Length && position - start < 32 && IsAsciiAlphaNumeric(value[position]))
        {
            position++;
        }
        if (position == start)
        {
            Append(destination, "&"u8);
            return position;
        }

        var hasSemicolon = position < value.Length && value[position] == (Byte)';';
        var candidate = value[start..(position + (hasSemicolon ? 1 : 0))];
        Span<Byte> replacement = stackalloc Byte[8];
        var entityLength = Utf8HtmlEntityDecoder.WriteLongestSymbolUtf8(candidate, replacement, out var matchedLength);
        if (entityLength != 0 && candidate[matchedLength - 1] != (Byte)';')
        {
            var followerIndex = start + matchedLength;
            if (
                followerIndex < value.Length
                && (value[followerIndex] == (Byte)'=' || IsAsciiAlphaNumeric(value[followerIndex]))
            )
            {
                entityLength = 0;
            }
        }
        if (entityLength != 0)
        {
            Append(destination, replacement[..entityLength]);
            Append(destination, candidate[matchedLength..]);
        }
        else
        {
            Append(destination, "&"u8);
            Append(destination, candidate);
        }
        return position + (hasSemicolon ? 1 : 0);
    }

    private Int32 DecodeNumericAttributeReference(ReadOnlySpan<Byte> value, Int32 index, Utf8TokenBuffer destination)
    {
        // index points at '&', index + 1 at '#'.
        var position = index + 2;
        var isHex = position < value.Length && (value[position] | 0x20) == (Byte)'x';
        if (isHex)
        {
            position++;
        }
        var radix = isHex ? 16u : 10u;
        var digitsStart = position;
        var scalar = 0u;
        var overflow = false;
        while (position < value.Length)
        {
            var digit = (UInt32)(value[position] - (Byte)'0');
            if (digit > 9)
            {
                if (!isHex)
                {
                    break;
                }
                digit = (UInt32)((value[position] | 0x20) - (Byte)'a');
                if (digit > 5)
                {
                    break;
                }
                digit += 10;
            }
            if (!overflow && scalar <= (0x10FFFFu - digit) / radix)
            {
                scalar = scalar * radix + digit;
            }
            else
            {
                overflow = true;
            }
            position++;
        }
        if (position == digitsStart)
        {
            // "&#" or "&#x" without digits is flushed raw; a terminating ';' goes with it.
            var end = position < value.Length && value[position] == (Byte)';' ? position + 1 : position;
            Append(destination, value[index..end]);
            return end;
        }
        if (position < value.Length && value[position] == (Byte)';')
        {
            position++;
        }

        Span<Byte> replacement = stackalloc Byte[4];
        Int32 length;
        if (overflow)
        {
            length = WriteReplacementCharacter(replacement);
        }
        else
        {
            var code = (Int32)scalar;
            var mapped = Utf8HtmlEntityDecoder.GetSymbolCodeFromTable(code);
            code = mapped < 0 ? code : mapped;
            length = Utf8HtmlEntityDecoder.IsInvalidNumber(code)
                ? WriteReplacementCharacter(replacement)
                : WriteScalarUtf8(code, replacement);
        }
        Append(destination, replacement[..length]);
        return position;
    }
}
