namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

/// <summary>
/// Decodes character references over a fully buffered attribute value. References in attribute
/// values cannot affect tokenization, so the tokenizer hands sinks the raw buffered bytes and the
/// consumer that actually reads a value runs this pass once, instead of every captured attribute
/// paying for a decode whether or not anything observes it. Mirrors the reference states in
/// attribute context, including the missing-semicolon suppression rule; the value's real
/// terminator (quote, space, or '&gt;') is never alphanumeric or '=', so the end of the buffer
/// never suppresses a match. Appends go straight to the destination buffer: callers account for
/// decoded bytes against their own capture limits.
/// </summary>
internal static class Utf8AttributeValueDecoder
{
    /// <summary>
    /// Decodes <paramref name="value"/> into <paramref name="destination"/>, where
    /// <paramref name="ampersand"/> is the index of the first '&amp;' in the value.
    /// </summary>
    internal static void Decode(ReadOnlySpan<Byte> value, Int32 ampersand, Utf8TokenBuffer destination)
    {
        var index = ampersand;
        destination.Append(value[..index]);
        while (true)
        {
            index = DecodeReference(value, index, destination);
            var run = value[index..].IndexOf((Byte)'&');
            if (run < 0)
            {
                destination.Append(value[index..]);
                return;
            }
            destination.Append(value.Slice(index, run));
            index += run;
        }
    }

    /// <summary>
    /// Decodes the single reference candidate whose '&amp;' sits at <paramref name="index"/>,
    /// appends its result to <paramref name="destination"/>, and returns the index of the
    /// first byte it did not consume.
    /// </summary>
    private static Int32 DecodeReference(ReadOnlySpan<Byte> value, Int32 index, Utf8TokenBuffer destination)
    {
        var position = index + 1;
        if (position < value.Length && value[position] == (Byte)'#')
        {
            return DecodeNumericReference(value, index, destination);
        }

        // The per-byte machine accumulates at most 32 alphanumeric candidate bytes.
        var start = position;
        while (position < value.Length && position - start < 32 && IsAsciiAlphaNumeric(value[position]))
        {
            position++;
        }
        if (position == start)
        {
            destination.Append("&"u8);
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
            destination.Append(replacement[..entityLength]);
            destination.Append(candidate[matchedLength..]);
        }
        else
        {
            destination.Append("&"u8);
            destination.Append(candidate);
        }
        return position + (hasSemicolon ? 1 : 0);
    }

    private static Int32 DecodeNumericReference(ReadOnlySpan<Byte> value, Int32 index, Utf8TokenBuffer destination)
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
            destination.Append(value[index..end]);
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
        destination.Append(replacement[..length]);
        return position;
    }

    internal static Int32 WriteReplacementCharacter(Span<Byte> destination)
    {
        "�"u8.CopyTo(destination);
        return 3;
    }

    internal static Int32 WriteScalarUtf8(Int32 scalar, Span<Byte> destination)
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

    private static Boolean IsAsciiAlphaNumeric(Byte value) =>
        (UInt32)(value - 'A') <= 'Z' - 'A' || (UInt32)(value - 'a') <= 'z' - 'a' || (UInt32)(value - '0') <= 9;
}
