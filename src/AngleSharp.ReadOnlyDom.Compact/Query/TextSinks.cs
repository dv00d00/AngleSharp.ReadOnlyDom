using System.Buffers;
using System.Text;

namespace AngleSharp.ReadOnlyDom.Compact;

/// <summary>
/// Accepts text chunks without requiring an intermediate string.
/// </summary>
public interface ISpanSink
{
    void Append(ReadOnlySpan<char> value);
}

public readonly struct StringBuilderSink(StringBuilder builder) : ISpanSink
{
    public void Append(ReadOnlySpan<char> value) => builder.Append(value);
}

public readonly struct TextWriterSink(TextWriter writer) : ISpanSink
{
    public void Append(ReadOnlySpan<char> value) => writer.Write(value);
}

public readonly struct BufferWriterSink(IBufferWriter<char> writer) : ISpanSink
{
    public void Append(ReadOnlySpan<char> value) => writer.Write(value);
}
