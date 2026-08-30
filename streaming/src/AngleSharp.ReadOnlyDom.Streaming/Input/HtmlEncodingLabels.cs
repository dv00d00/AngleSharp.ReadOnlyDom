using System.Text;

namespace AngleSharp.ReadOnlyDom.Streaming.Input;

internal static partial class HtmlEncodingLabels
{
    internal static bool TryResolve(string? label, out Encoding encoding)
    {
        var span = TrimAsciiWhitespace(label.AsSpan());
        if (!TryGetName(span, out var name) || name == "replacement")
        {
            encoding = null!;
            return false;
        }

        name = name switch
        {
            "ISO-8859-8-I" => "iso-8859-8",
            "x-user-defined" => "windows-1252",
            _ => name,
        };

        try
        {
            encoding = name switch
            {
                "UTF-8" => Encoding.UTF8,
                "UTF-16BE" => Encoding.BigEndianUnicode,
                "UTF-16LE" => Encoding.Unicode,
                _ => Encoding.GetEncoding(name),
            };
            return true;
        }
        catch (ArgumentException)
        {
            encoding = null!;
            return false;
        }
    }

    internal static bool TryParseContentType(string content, out Encoding encoding)
    {
        var remaining = content.AsSpan();
        while (true)
        {
            var index = remaining.IndexOf("charset", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                break;
            remaining = remaining[(index + "charset".Length)..];
            remaining = TrimAsciiWhitespaceStart(remaining);
            if (remaining.IsEmpty || remaining[0] != '=')
                continue;

            remaining = TrimAsciiWhitespaceStart(remaining[1..]);
            if (remaining.IsEmpty)
                break;

            ReadOnlySpan<char> label;
            if (remaining[0] is '\'' or '"')
            {
                var quote = remaining[0];
                remaining = remaining[1..];
                var end = remaining.IndexOf(quote);
                label = end < 0 ? remaining : remaining[..end];
            }
            else
            {
                var end = 0;
                while (end < remaining.Length && !IsAsciiWhitespace(remaining[end]) && remaining[end] != ';')
                    end++;
                label = remaining[..end];
            }

            return TryResolve(label.ToString(), out encoding);
        }

        encoding = null!;
        return false;
    }

    private static ReadOnlySpan<char> TrimAsciiWhitespace(ReadOnlySpan<char> value)
    {
        value = TrimAsciiWhitespaceStart(value);
        var length = value.Length;
        while (length > 0 && IsAsciiWhitespace(value[length - 1]))
            length--;
        return value[..length];
    }

    private static ReadOnlySpan<char> TrimAsciiWhitespaceStart(ReadOnlySpan<char> value)
    {
        var start = 0;
        while (start < value.Length && IsAsciiWhitespace(value[start]))
            start++;
        return value[start..];
    }

    private static bool IsAsciiWhitespace(char value) => value is '\t' or '\n' or '\f' or '\r' or ' ';
}
