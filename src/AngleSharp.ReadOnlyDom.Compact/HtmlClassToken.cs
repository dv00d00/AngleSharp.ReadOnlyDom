namespace AngleSharp.ReadOnlyDom.Compact;

internal static class HtmlClassToken
{
    internal static bool Contains(ReadOnlySpan<char> tokens, ReadOnlySpan<char> wanted)
    {
        var index = 0;
        while (index < tokens.Length)
        {
            while (index < tokens.Length && IsSpace(tokens[index]))
                index++;
            var start = index;
            while (index < tokens.Length && !IsSpace(tokens[index]))
                index++;
            if (index > start && tokens[start..index].SequenceEqual(wanted))
                return true;
        }

        return false;
    }

    internal static void Validate(string token, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(token, parameterName);
        Validate(token.AsSpan(), parameterName);
    }

    internal static void Validate(ReadOnlySpan<char> token, string parameterName)
    {
        if (token.IsEmpty)
            throw new ArgumentException("A class token cannot be empty.", parameterName);
        foreach (var character in token)
            if (IsSpace(character))
                throw new ArgumentException("A class token cannot contain HTML whitespace.", parameterName);
    }

    internal static bool IsSpace(char value) => value is '\t' or '\n' or '\f' or '\r' or ' ';
}
