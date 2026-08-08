using System.Buffers;

namespace AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;

internal static class StartTagMutationWriter
{
    internal static byte[] Rewrite(ReadOnlySpan<byte> source, HtmlElementMutation mutation)
    {
        var output = new ArrayBufferWriter<byte>(source.Length + 64);
        var close = source.Length - 1;
        if (close < 1 || source[0] != (byte)'<' || source[close] != (byte)'>')
            throw new InvalidOperationException("A start-tag rewrite range does not contain a complete tag.");

        var slash = mutation.SelfClosingSyntax ? FindSelfClosingSlash(source, close) : -1;
        var attributesEnd = slash >= 0 ? slash : close;
        var attributes = ParseAttributes(source, attributesEnd);
        var operationEmitted = mutation.Attributes.Count == 0 ? [] : new bool[mutation.Attributes.Count];
        var cursor = 0;
        foreach (var attribute in attributes)
        {
            var operation = FindLastOperation(mutation.Attributes, source[attribute.NameStart..attribute.NameEnd]);
            HtmlRewritePayload.Write(output, source[cursor..attribute.PrefixStart]);
            if (operation < 0)
            {
                HtmlRewritePayload.Write(output, source[attribute.PrefixStart..attribute.End]);
            }
            else
            {
                var edit = mutation.Attributes[operation];
                if (edit.Kind == AttributeMutationKind.Set && !operationEmitted[operation])
                {
                    HtmlRewritePayload.Write(output, source[attribute.PrefixStart..attribute.NameStart]);
                    HtmlRewritePayload.WriteAttribute(output, edit.Name, edit.Value!);
                    operationEmitted[operation] = true;
                }
            }
            cursor = attribute.End;
        }

        HtmlRewritePayload.Write(output, source[cursor..attributesEnd]);
        for (var index = 0; index < mutation.Attributes.Count; index++)
        {
            var edit = mutation.Attributes[index];
            if (edit.Kind == AttributeMutationKind.Remove || operationEmitted[index])
                continue;
            if (edit.Kind == AttributeMutationKind.Set && HasLaterSetOrRemove(mutation.Attributes, index, edit.Name))
                continue;
            if (output.WrittenCount == 0 || !Utf8RewriteCollector.IsHtmlSpace(output.WrittenSpan[^1]))
                HtmlRewritePayload.Write(output, " "u8);
            HtmlRewritePayload.WriteAttribute(output, edit.Name, edit.Value!);
        }

        if (slash >= 0 && !ShouldDropSelfClosingSlash(mutation))
            HtmlRewritePayload.Write(output, source[slash..]);
        else if (slash >= 0)
            HtmlRewritePayload.Write(output, source[(slash + 1)..]);
        else
            HtmlRewritePayload.Write(output, source[close..]);
        return output.WrittenSpan.ToArray();
    }

    private static bool ShouldDropSelfClosingSlash(HtmlElementMutation mutation) =>
        mutation.CanHaveContent
        && (mutation.Prepend.Count != 0 || mutation.Append.Count != 0 || mutation.SuppressInnerContent);

    private static int FindSelfClosingSlash(ReadOnlySpan<byte> source, int close)
    {
        var index = close - 1;
        while (index > 0 && Utf8RewriteCollector.IsHtmlSpace(source[index]))
            index--;
        return source[index] == (byte)'/' ? index : -1;
    }

    private static List<ParsedAttribute> ParseAttributes(ReadOnlySpan<byte> source, int end)
    {
        var attributes = new List<ParsedAttribute>();
        var index = 1;
        while (index < end && !IsTagNameEnd(source[index]))
            index++;

        while (index < end)
        {
            var prefixStart = index;
            while (index < end && Utf8RewriteCollector.IsHtmlSpace(source[index]))
                index++;
            if (index >= end)
                break;
            if (source[index] == (byte)'/')
            {
                index++;
                continue;
            }

            var nameStart = index;
            while (index < end && !IsAttributeNameEnd(source[index]))
                index++;
            var nameEnd = index;
            if (nameEnd == nameStart)
            {
                index++;
                continue;
            }
            while (index < end && Utf8RewriteCollector.IsHtmlSpace(source[index]))
                index++;
            if (index < end && source[index] == (byte)'=')
            {
                index++;
                while (index < end && Utf8RewriteCollector.IsHtmlSpace(source[index]))
                    index++;
                if (index < end && source[index] is (byte)'"' or (byte)'\'')
                {
                    var quote = source[index++];
                    while (index < end && source[index] != quote)
                        index++;
                    if (index < end)
                        index++;
                }
                else
                {
                    while (index < end && !Utf8RewriteCollector.IsHtmlSpace(source[index]))
                        index++;
                }
            }
            attributes.Add(new ParsedAttribute(prefixStart, nameStart, nameEnd, index));
        }
        return attributes;
    }

    private static int FindLastOperation(List<AttributeMutation> operations, ReadOnlySpan<byte> name)
    {
        for (var index = operations.Count - 1; index >= 0; index--)
        {
            var operation = operations[index];
            if (operation.Kind != AttributeMutationKind.Append && AsciiEqualsIgnoreCase(operation.Name, name))
                return index;
        }
        return -1;
    }

    private static bool HasLaterSetOrRemove(List<AttributeMutation> operations, int current, ReadOnlySpan<byte> name)
    {
        for (var index = current + 1; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Kind != AttributeMutationKind.Append && AsciiEqualsIgnoreCase(operation.Name, name))
                return true;
        }
        return false;
    }

    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
            return false;
        for (var index = 0; index < left.Length; index++)
        {
            var a = left[index];
            var b = right[index];
            if (a is >= (byte)'A' and <= (byte)'Z')
                a += (byte)('a' - 'A');
            if (b is >= (byte)'A' and <= (byte)'Z')
                b += (byte)('a' - 'A');
            if (a != b)
                return false;
        }
        return true;
    }

    private static bool IsTagNameEnd(byte value) =>
        Utf8RewriteCollector.IsHtmlSpace(value) || value is (byte)'/' or (byte)'>';

    private static bool IsAttributeNameEnd(byte value) =>
        Utf8RewriteCollector.IsHtmlSpace(value) || value is (byte)'/' or (byte)'>' or (byte)'=';

    private readonly record struct ParsedAttribute(int PrefixStart, int NameStart, int NameEnd, int End);
}
