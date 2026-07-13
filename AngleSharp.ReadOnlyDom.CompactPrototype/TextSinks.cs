using System.Buffers;
using System.Text;

namespace AngleSharp.ReadOnlyDom.CompactPrototype;

/// <summary>
/// A sink that accepts <see cref="ReadOnlySpan{Char}"/> chunks. Node text extraction streams the text
/// nodes' value spans (slices into the document's shared buffer) straight into the sink, so no
/// intermediate string is built. Implement this on a struct and pass it by ref to
/// <see cref="Node.WriteText{TSink}(ref TSink)"/> for a custom, allocation-free destination.
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

/// <summary>Measures total text length without materializing it (e.g. to size a buffer for <c>TryWriteText</c>).</summary>
public struct LengthSink : ISpanSink
{
    public int Length;

    public void Append(ReadOnlySpan<char> value) => Length += value.Length;
}
