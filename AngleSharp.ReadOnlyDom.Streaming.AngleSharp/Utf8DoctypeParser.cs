using System.Text;
using AngleSharp.Html.Parser.Tokens.Struct;

namespace AngleSharp.ReadOnlyDom.Streaming.AngleSharp;

internal static class Utf8DoctypeParser
{
    public static StructHtmlToken Parse(ReadOnlySpan<byte> declaration)
    {
        var index = 0;
        SkipWhitespace(declaration, ref index);
        var nameStart = index;
        while (index < declaration.Length && !IsWhitespace(declaration[index]))
            index++;

        var name = Encoding.UTF8.GetString(declaration[nameStart..index]).ToLowerInvariant();
        var token = StructHtmlToken.Doctype(name.Length == 0, default);
        token.Name = name;
        SkipWhitespace(declaration, ref index);
        if (index == declaration.Length)
            return token;

        if (ConsumeKeyword(declaration, ref index, "public"u8))
        {
            SkipWhitespace(declaration, ref index);
            if (!ReadQuoted(declaration, ref index, out var publicIdentifier, out var publicClosed))
            {
                token.IsQuirksForced = true;
                return token;
            }

            token.PublicIdentifier = publicIdentifier;
            if (!publicClosed)
            {
                token.IsQuirksForced = true;
                return token;
            }

            SkipWhitespace(declaration, ref index);
            if (index == declaration.Length)
                return token;

            if (ReadQuoted(declaration, ref index, out var systemIdentifier, out var systemClosed))
            {
                token.SystemIdentifier = systemIdentifier;
                token.IsQuirksForced = !systemClosed || HasNonWhitespaceRemainder(declaration, index);
            }
            else
            {
                token.IsQuirksForced = true;
            }

            return token;
        }

        if (ConsumeKeyword(declaration, ref index, "system"u8))
        {
            SkipWhitespace(declaration, ref index);
            if (ReadQuoted(declaration, ref index, out var systemIdentifier, out var systemClosed))
            {
                token.SystemIdentifier = systemIdentifier;
                token.IsQuirksForced = !systemClosed || HasNonWhitespaceRemainder(declaration, index);
            }
            else
            {
                token.IsQuirksForced = true;
            }

            return token;
        }

        token.IsQuirksForced = true;
        return token;
    }

    private static bool ReadQuoted(
        ReadOnlySpan<byte> declaration,
        ref int index,
        out string identifier,
        out bool closed)
    {
        identifier = String.Empty;
        closed = false;
        if (index >= declaration.Length || declaration[index] is not ((byte)'\'' or (byte)'"'))
            return false;

        var quote = declaration[index++];
        var start = index;
        while (index < declaration.Length && declaration[index] != quote)
            index++;

        identifier = Encoding.UTF8.GetString(declaration[start..index]);
        if (index < declaration.Length)
        {
            index++;
            closed = true;
        }

        return true;
    }

    private static bool ConsumeKeyword(ReadOnlySpan<byte> declaration, ref int index, ReadOnlySpan<byte> keyword)
    {
        if (declaration.Length - index < keyword.Length)
            return false;
        for (var offset = 0; offset < keyword.Length; offset++)
        {
            if (AsciiLower(declaration[index + offset]) != keyword[offset])
                return false;
        }

        index += keyword.Length;
        return true;
    }

    private static bool HasNonWhitespaceRemainder(ReadOnlySpan<byte> declaration, int index)
    {
        SkipWhitespace(declaration, ref index);
        return index != declaration.Length;
    }

    private static void SkipWhitespace(ReadOnlySpan<byte> declaration, ref int index)
    {
        while (index < declaration.Length && IsWhitespace(declaration[index]))
            index++;
    }

    private static bool IsWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0C;

    private static byte AsciiLower(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 0x20) : value;
}
