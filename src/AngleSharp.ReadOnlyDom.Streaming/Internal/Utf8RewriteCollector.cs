using System.Buffers;

namespace AngleSharp.ReadOnlyDom.Streaming;

internal sealed class Utf8RewriteCollector
{
    private readonly ArrayBufferWriter<byte> _payload = new(256);
    private readonly List<Insertion> _insertions = [];

    internal void AppendAttribute(
        long sourceStart,
        long sourceEnd,
        bool selfClosing,
        ReadOnlySpan<byte> name,
        ReadOnlySpan<byte> value
    )
    {
        ValidateName(name);
        var payloadStart = _payload.WrittenCount;
        Write(name);
        Write("=\""u8);
        foreach (var item in value)
        {
            if (item == (byte)'&')
                Write("&amp;"u8);
            else if (item == (byte)'"')
                Write("&quot;"u8);
            else
                WriteByte(item);
        }
        WriteByte((byte)'"');
        _insertions.Add(
            new Insertion(sourceStart, sourceEnd, selfClosing, payloadStart, _payload.WrittenCount - payloadStart)
        );
    }

    internal void WriteTo(ReadOnlySpan<byte> source, IBufferWriter<byte> output)
    {
        var cursor = 0;
        foreach (var insertion in _insertions)
        {
            var sourceStart = checked((int)insertion.SourceStart);
            var sourceEnd = checked((int)insertion.SourceEnd);
            if (sourceStart < 0 || sourceEnd > source.Length || sourceEnd <= sourceStart || source[sourceStart] != '<')
                throw new InvalidOperationException("A recorded start-tag source range is outside the input.");
            var position = sourceEnd - 1;
            if (source[position] != '>')
                throw new InvalidOperationException("A recorded start-tag source range does not end at a tag close.");
            if (insertion.SelfClosing && position > sourceStart && source[position - 1] == '/')
                position--;
            if (position < cursor)
                throw new InvalidOperationException("Start-tag rewrite ranges are not ordered.");

            Write(output, source[cursor..position]);
            if (position == cursor || position == sourceStart || !IsHtmlSpace(source[position - 1]))
                Write(output, " "u8);
            Write(output, _payload.WrittenSpan.Slice(insertion.PayloadStart, insertion.PayloadLength));
            cursor = position;
        }
        Write(output, source[cursor..]);
    }

    private static bool IsHtmlSpace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\f' or (byte)'\r';

    private static void ValidateName(ReadOnlySpan<byte> name)
    {
        if (name.IsEmpty)
            throw new ArgumentException("An attribute name cannot be empty.", nameof(name));
        foreach (var item in name)
        {
            if (item <= 0x20 || item is (byte)'"' or (byte)'\'' or (byte)'/' or (byte)'>' or (byte)'=')
                throw new ArgumentException("The attribute name contains an HTML delimiter.", nameof(name));
        }
    }

    private void WriteByte(byte value)
    {
        _payload.GetSpan(1)[0] = value;
        _payload.Advance(1);
    }

    private void Write(ReadOnlySpan<byte> value)
    {
        value.CopyTo(_payload.GetSpan(value.Length));
        _payload.Advance(value.Length);
    }

    private static void Write(IBufferWriter<byte> output, ReadOnlySpan<byte> value)
    {
        value.CopyTo(output.GetSpan(value.Length));
        output.Advance(value.Length);
    }

    private readonly record struct Insertion(
        long SourceStart,
        long SourceEnd,
        bool SelfClosing,
        int PayloadStart,
        int PayloadLength
    );
}
