using System.Buffers;
using System.Text;

namespace AngleSharp.ReadOnlyDom.Compact.Query;

/// <summary>
///     Accepts text chunks without requiring an intermediate string.
/// </summary>
internal interface ISpanSink
{
    void Append(ReadOnlySpan<char> value);
}

internal readonly struct StringBuilderSink(StringBuilder builder) : ISpanSink
{
    public void Append(ReadOnlySpan<char> value)
    {
        builder.Append(value);
    }
}

internal readonly struct TextWriterSink(TextWriter writer) : ISpanSink
{
    public void Append(ReadOnlySpan<char> value)
    {
        writer.Write(value);
    }
}

internal readonly struct BufferWriterSink(IBufferWriter<char> writer) : ISpanSink
{
    public void Append(ReadOnlySpan<char> value)
    {
        writer.Write(value);
    }
}
