using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom;

internal static class Shims
{
    public static void WriteSOM(this TextWriter writer, StringOrMemory content)
    {
        #if NETSTANDARD2_0
        writer.Write(content.ToString());
        #else
        writer.Write(content.Memory.Span);
        #endif
    }

    public static void AppendSpan(this System.Text.StringBuilder sb, ReadOnlySpan<char> span)
    {
        #if NETSTANDARD2_0
        sb.Append(span.ToString());
        #else
        sb.Append(span);
        #endif
    }
}