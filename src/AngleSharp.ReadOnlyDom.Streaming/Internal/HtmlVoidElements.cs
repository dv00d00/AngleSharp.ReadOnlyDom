namespace AngleSharp.ReadOnlyDom.Streaming;

internal static class HtmlVoidElements
{
    internal static readonly ulong Area = Compact("area"u8);
    internal static readonly ulong Base = Compact("base"u8);
    internal static readonly ulong Br = Compact("br"u8);
    internal static readonly ulong Col = Compact("col"u8);
    internal static readonly ulong Embed = Compact("embed"u8);
    internal static readonly ulong Hr = Compact("hr"u8);
    internal static readonly ulong Img = Compact("img"u8);
    internal static readonly ulong Input = Compact("input"u8);
    internal static readonly ulong Link = Compact("link"u8);
    internal static readonly ulong Meta = Compact("meta"u8);
    internal static readonly ulong Param = Compact("param"u8);
    internal static readonly ulong Source = Compact("source"u8);
    internal static readonly ulong Track = Compact("track"u8);
    internal static readonly ulong Wbr = Compact("wbr"u8);

    private static ulong Compact(ReadOnlySpan<byte> name)
    {
        if (!Utf8HtmlName.TryGetCompactKey(name, out var key))
            throw new InvalidOperationException("An HTML void name was not compact-representable.");
        return key;
    }
}
