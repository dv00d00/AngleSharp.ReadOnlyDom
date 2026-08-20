using System.Buffers;

namespace AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;

internal enum ElementDisposition : byte
{
    Normal,
    Remove,
    Replace,
    Unwrap,
}

internal enum AttributeMutationKind : byte
{
    Append,
    Set,
    Remove,
}

internal sealed class HtmlElementMutation(long sourceStart, long sourceEnd, bool canHaveContent, bool selfClosingSyntax)
{
    internal long SourceStart { get; } = sourceStart;
    internal long SourceEnd { get; } = sourceEnd;
    internal bool CanHaveContent { get; } = canHaveContent;
    internal bool SelfClosingSyntax { get; } = selfClosingSyntax;

    // Allocated on first use. A mutation typically touches one of these five, so constructing them
    // all eagerly cost five allocations per mutated element before any payload was copied.
    internal List<AttributeMutation>? Attributes { get; set; }
    internal List<byte[]>? Before { get; set; }
    internal List<byte[]>? Prepend { get; set; }
    internal List<byte[]>? Append { get; set; }
    internal List<byte[]>? After { get; set; }
    internal ElementDisposition Disposition { get; set; }
    internal byte[]? Replacement { get; set; }
    internal bool SuppressInnerContent { get; set; }
    internal byte[]? InnerReplacement { get; set; }
    internal long EndStart { get; set; } = -1;
    internal long EndEnd { get; set; } = -1;
    internal bool HasExplicitEndTag { get; set; }
    internal bool Ignored { get; set; }
    internal bool OpensSuppression { get; set; }
    internal bool RequiresEndTagRange { get; set; }
    internal int StartSequence { get; set; }
    internal int EndSequence { get; set; }

    internal bool ChangesStartTag =>
        Attributes is { Count: > 0 }
        || (SelfClosingSyntax && CanHaveContent && (Prepend is { Count: > 0 } || SuppressInnerContent));
}

internal readonly record struct AttributeMutation(AttributeMutationKind Kind, byte[] Name, byte[]? Value);

internal static class HtmlRewritePayload
{
    internal static byte[] CopyContent(ReadOnlySpan<byte> content, HtmlRewriteContentType contentType)
    {
        if (contentType == HtmlRewriteContentType.Html)
            return content.ToArray();
        if (contentType != HtmlRewriteContentType.Text)
            throw new ArgumentOutOfRangeException(nameof(contentType));

        var first = content.IndexOfAny((byte)'&', (byte)'<', (byte)'>');
        if (first < 0)
            return content.ToArray();
        var output = new ArrayBufferWriter<byte>(content.Length + 16);
        Write(output, content[..first]);
        foreach (var item in content[first..])
        {
            if (item == (byte)'&')
                Write(output, "&amp;"u8);
            else if (item == (byte)'<')
                Write(output, "&lt;"u8);
            else if (item == (byte)'>')
                Write(output, "&gt;"u8);
            else
            {
                output.GetSpan(1)[0] = item;
                output.Advance(1);
            }
        }
        return output.WrittenSpan.ToArray();
    }

    internal static void ValidateAttributeName(ReadOnlySpan<byte> name)
    {
        if (name.IsEmpty)
            throw new ArgumentException("An attribute name cannot be empty.", nameof(name));
        foreach (var item in name)
        {
            if (item <= 0x20 || item is (byte)'"' or (byte)'\'' or (byte)'/' or (byte)'>' or (byte)'=')
                throw new ArgumentException("The attribute name contains an HTML delimiter.", nameof(name));
        }
    }

    internal static byte[] CopyAttributeValue(ReadOnlySpan<byte> value) => value.ToArray();

    internal static void WriteAttribute(IBufferWriter<byte> output, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        Write(output, name);
        Write(output, "=\""u8);
        foreach (var item in value)
        {
            if (item == (byte)'&')
                Write(output, "&amp;"u8);
            else if (item == (byte)'"')
                Write(output, "&quot;"u8);
            else
            {
                output.GetSpan(1)[0] = item;
                output.Advance(1);
            }
        }
        output.GetSpan(1)[0] = (byte)'"';
        output.Advance(1);
    }

    internal static void Write(IBufferWriter<byte> output, ReadOnlySpan<byte> value)
    {
        value.CopyTo(output.GetSpan(value.Length));
        output.Advance(value.Length);
    }
}
