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

    public static void AppendSOM(this System.Text.StringBuilder sb, StringOrMemory content)
    {
#if NETSTANDARD2_0
        sb.Append(content.ToString());
#else
        sb.Append(content.Memory.Span);
#endif
    }
    
    public static void AppendSpan(this System.Text.StringBuilder sb, ReadOnlySpan<char> content)
    {
#if NETSTANDARD2_0
        sb.Append(content.ToString());
#else
        sb.Append(content);
#endif
    }
}