using System.Buffers;

namespace AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;

/// <summary>Receives start-tag edits recorded through a <see cref="StartTagEditor"/>.</summary>
internal interface IStartTagEditCollector
{
    void AppendAttribute(
        long sourceStart,
        long sourceEnd,
        bool selfClosing,
        ReadOnlySpan<byte> name,
        ReadOnlySpan<byte> value
    );
}

internal sealed class Utf8RewriteCollector : IStartTagEditCollector
{
    private readonly ArrayBufferWriter<byte> _payload = new(256);
    private readonly List<Insertion> _insertions = [];

    public void AppendAttribute(
        long sourceStart,
        long sourceEnd,
        bool selfClosing,
        ReadOnlySpan<byte> name,
        ReadOnlySpan<byte> value
    )
    {
        ValidateName(name);
        var payloadStart = _payload.WrittenCount;
        WriteAttributePayload(_payload, name, value);
        _insertions.Add(
            new Insertion(sourceStart, sourceEnd, selfClosing, payloadStart, _payload.WrittenCount - payloadStart)
        );
    }

    internal static void WriteAttributePayload(
        IBufferWriter<byte> output,
        ReadOnlySpan<byte> name,
        ReadOnlySpan<byte> value
    )
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
                WriteByte(output, item);
        }
        WriteByte(output, (byte)'"');
    }

    internal void WriteTo(ReadOnlySpan<byte> source, IBufferWriter<byte> output)
    {
        var segments = GetSegments(source);
        while (segments.MoveNext())
        {
            Write(output, segments.Current);
        }
    }

    internal void WriteTo<TState>(ReadOnlySpan<byte> source, ref TState state, RewriteSegmentSink<TState> sink)
    {
        var segments = GetSegments(source);
        while (segments.MoveNext())
        {
            sink(ref state, segments.Current);
        }
    }

    private SegmentEnumerator GetSegments(ReadOnlySpan<byte> source) => new(this, source);

    /// <summary>
    /// Walks the rewritten document as a sequence of segments, each either a borrowed slice of the
    /// source, a separator, or a borrowed slice of the recorded payload - no byte is copied.
    /// </summary>
    internal ref struct SegmentEnumerator
    {
        private readonly Utf8RewriteCollector _collector;
        private readonly ReadOnlySpan<byte> _source;
        private int _insertionIndex;
        private int _cursor;
        private int _phase;
        private int _position;

        internal SegmentEnumerator(Utf8RewriteCollector collector, ReadOnlySpan<byte> source)
        {
            _collector = collector;
            _source = source;
        }

        public ReadOnlySpan<byte> Current { get; private set; }

        public bool MoveNext()
        {
            var source = _source;
            while (true)
            {
                if (_insertionIndex >= _collector._insertions.Count)
                {
                    if (_phase == 3 || _cursor == source.Length)
                        return false;
                    _phase = 3;
                    Current = source[_cursor..];
                    return true;
                }

                var insertion = _collector._insertions[_insertionIndex];
                switch (_phase)
                {
                    case 0:
                        var sourceStart = checked((int)insertion.SourceStart);
                        var sourceEnd = checked((int)insertion.SourceEnd);
                        if (
                            sourceStart < 0
                            || sourceEnd > source.Length
                            || sourceEnd <= sourceStart
                            || source[sourceStart] != '<'
                        )
                        {
                            throw new InvalidOperationException(
                                "A recorded start-tag source range is outside the input."
                            );
                        }
                        _position = sourceEnd - 1;
                        if (source[_position] != '>')
                        {
                            throw new InvalidOperationException(
                                "A recorded start-tag source range does not end at a tag close."
                            );
                        }
                        if (insertion.SelfClosing && _position > sourceStart && source[_position - 1] == '/')
                            _position--;
                        if (_position < _cursor)
                            throw new InvalidOperationException("Start-tag rewrite ranges are not ordered.");

                        var run = source[_cursor.._position];
                        var needsSeparator =
                            _position == _cursor || _position == sourceStart || !IsHtmlSpace(source[_position - 1]);
                        _phase = needsSeparator ? 1 : 2;
                        if (!run.IsEmpty)
                        {
                            Current = run;
                            return true;
                        }
                        continue;
                    case 1:
                        _phase = 2;
                        Current = " "u8;
                        return true;
                    default:
                        Current = _collector._payload.WrittenSpan.Slice(insertion.PayloadStart, insertion.PayloadLength);
                        _cursor = _position;
                        _insertionIndex++;
                        _phase = 0;
                        return true;
                }
            }
        }
    }

    internal static bool IsHtmlSpace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\f' or (byte)'\r';

    internal static void ValidateName(ReadOnlySpan<byte> name)
    {
        if (name.IsEmpty)
            throw new ArgumentException("An attribute name cannot be empty.", nameof(name));
        foreach (var item in name)
        {
            if (item <= 0x20 || item is (byte)'"' or (byte)'\'' or (byte)'/' or (byte)'>' or (byte)'=')
                throw new ArgumentException("The attribute name contains an HTML delimiter.", nameof(name));
        }
    }

    private static void WriteByte(IBufferWriter<byte> output, byte value)
    {
        output.GetSpan(1)[0] = value;
        output.Advance(1);
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
