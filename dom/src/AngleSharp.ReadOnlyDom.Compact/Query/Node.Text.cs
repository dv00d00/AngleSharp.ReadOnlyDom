using System.Buffers;
using System.Text;
using AngleSharp.ReadOnlyDom.Compact.Document;

namespace AngleSharp.ReadOnlyDom.Compact.Query;

public readonly partial struct Node
{
    public string Text()
    {
        return string.Create(
            TextLength(),
            this,
            static (destination, node) =>
            {
                if (!node.TryWriteText(destination, out var written) || written != destination.Length)
                    throw new InvalidOperationException("Descendant text length changed while materializing text.");
            }
        );
    }

    /// <summary>Total length of this node's descendant text, without materializing it.</summary>
    public int TextLength()
    {
        var sink = new LengthSink();
        WriteText(ref sink);
        return sink.Length;
    }

    /// <summary>
    ///     Streams this node's descendant text into <paramref name="sink" /> as span chunks — no intermediate
    ///     string. The sink is a by-ref struct so mutations (e.g. accumulated length) persist and there is no
    ///     allocation or boxing; the JIT specializes the walk per sink type.
    /// </summary>
    internal void WriteText<TSink>(ref TSink sink)
        where TSink : ISpanSink
    {
        var document = Document;
        var endExclusive = document.SubtreeEndAt(Handle);
        for (var handle = Handle; handle < endExclusive; handle++)
        {
            var kind = document.KindAt(handle);
            if (kind == CompactNodeKind.Element && document.IsTemplate(handle))
            {
                handle = document.SubtreeEndAt(handle) - 1;
                continue;
            }

            if (kind != CompactNodeKind.Text)
                continue;
            var payloadIndex = document.PayloadIndexAt(handle);
            if (payloadIndex < 0)
                continue;
            sink.Append(document.PayloadValueSpanAt(payloadIndex));
        }
    }

    public void AppendText(StringBuilder builder)
    {
        var sink = new StringBuilderSink(builder);
        WriteText(ref sink);
    }

    public void WriteText(TextWriter writer)
    {
        var sink = new TextWriterSink(writer);
        WriteText(ref sink);
    }

    public void WriteText(IBufferWriter<char> writer)
    {
        var sink = new BufferWriterSink(writer);
        WriteText(ref sink);
    }

    /// <summary>
    ///     Copies descendant text into <paramref name="destination" /> (e.g. a stackalloc buffer). Returns false
    ///     without a partial guarantee if it does not fit; size with <see cref="TextLength" /> first.
    /// </summary>
    public bool TryWriteText(Span<char> destination, out int written)
    {
        written = 0;
        return WriteInto(destination, ref written);
    }

    private bool WriteInto(Span<char> destination, ref int written)
    {
        var document = Document;
        var endExclusive = document.SubtreeEndAt(Handle);
        for (var handle = Handle; handle < endExclusive; handle++)
        {
            var kind = document.KindAt(handle);
            if (kind == CompactNodeKind.Element && document.IsTemplate(handle))
            {
                handle = document.SubtreeEndAt(handle) - 1;
                continue;
            }

            if (kind != CompactNodeKind.Text)
                continue;
            var payloadIndex = document.PayloadIndexAt(handle);
            if (payloadIndex < 0)
                continue;
            var value = document.PayloadValueSpanAt(payloadIndex);
            if (written + value.Length > destination.Length)
                return false;
            value.CopyTo(destination.Slice(written));
            written += value.Length;
        }

        return true;
    }

    private struct LengthSink : ISpanSink
    {
        internal int Length;

        public void Append(ReadOnlySpan<char> value)
        {
            Length += value.Length;
        }
    }
}
