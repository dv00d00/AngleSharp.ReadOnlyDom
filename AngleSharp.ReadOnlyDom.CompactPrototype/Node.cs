using System.Buffers;
using System.Text;

namespace AngleSharp.ReadOnlyDom.CompactPrototype;

/// <summary>
/// A zero-allocation cursor over a single node in a <see cref="CompactDocument"/> — just a
/// (document, handle) pair. It is the ergonomic unit both query surfaces return: the familiar
/// descendant walk (<see cref="CompactQuery.Descendants"/>) and the SIMD-friendly tag scan
/// (<see cref="CompactQuery.Elements(CompactDocument, string)"/>). Names resolve either by string
/// (convenient) or by a pre-resolved id from <see cref="CompactQuery.Name"/> (for hot loops).
/// </summary>
public readonly struct Node
{
    private readonly CompactDocument? _document;
    private readonly int _handle;

    internal Node(CompactDocument document, int handle)
    {
        _document = document;
        _handle = handle;
    }

    public bool Exists => _document is not null && _handle >= 0;
    public int Handle => _handle;
    public CompactDocument Document => _document!;

    private CompactNode Raw => _document!.GetNode(_handle);

    public CompactNodeKind Kind => _document!.KindAt(_handle);
    public bool IsElement => _document!.KindAt(_handle) == CompactNodeKind.Element;
    public ushort NameId => _document!.NameIdAt(_handle);
    public string Name => _document!.GetName(_document.NameIdAt(_handle));

    public ReadOnlySpan<char> LocalName
    {
        get
        {
            var name = Name.AsSpan();
            var separator = name.IndexOf(':');
            return separator < 0 ? name : name[(separator + 1)..];
        }
    }

    /// <summary>True when this is an element named <paramref name="tag"/>.</summary>
    public bool Is(string tag) => Is(_document!.ResolveNameId(tag));

    /// <summary>Span overload — resolves without allocating a name string.</summary>
    public bool Is(ReadOnlySpan<char> tag) => Is(_document!.ResolveNameId(tag));

    /// <summary>Id-based overload for hot loops; resolve once with <see cref="CompactQuery.Name"/>.</summary>
    public bool Is(ushort tagId) =>
        tagId != ushort.MaxValue
        && _document!.KindAt(_handle) == CompactNodeKind.Element
        && _document.NameIdAt(_handle) == tagId;

    /// <summary>The parent node, or a non-existent cursor when parent links were not retained.</summary>
    public Node Parent =>
        _document is not null && _document.HasParentLinks
            ? new Node(_document, _document.GetParent(_handle))
            : default;

    /// <summary>The attribute value (empty span if absent — use <see cref="HasAttr(string)"/> to disambiguate).</summary>
    public ReadOnlySpan<char> Attr(string name) => Attr(name.AsSpan());

    public ReadOnlySpan<char> Attr(ReadOnlySpan<char> name) =>
        TryFindAttribute(_document!.ResolveNameId(name), out var value) ? value : default;

    public bool HasAttr(string name) => HasAttr(name.AsSpan());

    public bool HasAttr(ReadOnlySpan<char> name) => TryFindAttribute(_document!.ResolveNameId(name), out _);

    public bool HasClass(string token) => HasClass(token.AsSpan());

    public bool HasClass(ReadOnlySpan<char> token)
    {
        if (!TryFindAttribute(_document!.ResolveNameId("class"), out var classes))
            return false;
        return ContainsToken(classes, token);
    }

    public string Text()
    {
        var builder = new StringBuilder(TextLength());
        AppendText(builder);
        return builder.ToString();
    }

    /// <summary>Total length of this node's descendant text, without materializing it.</summary>
    public int TextLength()
    {
        var sink = new LengthSink();
        WriteText(ref sink);
        return sink.Length;
    }

    /// <summary>
    /// Streams this node's descendant text into <paramref name="sink"/> as span chunks — no intermediate
    /// string. The sink is a by-ref struct so mutations (e.g. accumulated length) persist and there is no
    /// allocation or boxing; the JIT specializes the walk per sink type.
    /// </summary>
    public void WriteText<TSink>(ref TSink sink)
        where TSink : ISpanSink
    {
        var node = Raw;
        if (node.Kind == CompactNodeKind.Text && node.PayloadIndex >= 0)
        {
            var payload = _document!.GetPayload(node.PayloadIndex);
            sink.Append(_document.GetValue(payload.ValueStart, payload.ValueLength));
        }
        for (var child = node.FirstChild; child >= 0; child = _document!.GetNode(child).NextSibling)
            new Node(_document!, child).WriteText(ref sink);
    }

    /// <summary>Appends descendant text into <paramref name="builder"/> without allocating a string.</summary>
    public void AppendText(StringBuilder builder)
    {
        var sink = new StringBuilderSink(builder);
        WriteText(ref sink);
    }

    /// <summary>Writes descendant text directly to a <see cref="TextWriter"/> from the value spans.</summary>
    public void WriteText(TextWriter writer)
    {
        var sink = new TextWriterSink(writer);
        WriteText(ref sink);
    }

    /// <summary>Writes descendant text into an <see cref="IBufferWriter{Char}"/> from the value spans.</summary>
    public void WriteText(IBufferWriter<char> writer)
    {
        var sink = new BufferWriterSink(writer);
        WriteText(ref sink);
    }

    /// <summary>
    /// Copies descendant text into <paramref name="destination"/> (e.g. a stackalloc buffer). Returns false
    /// without a partial guarantee if it does not fit; size with <see cref="TextLength"/> first.
    /// </summary>
    public bool TryWriteText(Span<char> destination, out int written)
    {
        written = 0;
        return WriteInto(destination, ref written);
    }

    private bool WriteInto(Span<char> destination, ref int written)
    {
        var node = Raw;
        if (node.Kind == CompactNodeKind.Text && node.PayloadIndex >= 0)
        {
            var payload = _document!.GetPayload(node.PayloadIndex);
            var value = _document.GetValue(payload.ValueStart, payload.ValueLength);
            if (written + value.Length > destination.Length)
                return false;
            value.CopyTo(destination.Slice(written));
            written += value.Length;
        }
        for (var child = node.FirstChild; child >= 0; child = _document!.GetNode(child).NextSibling)
            if (!new Node(_document!, child).WriteInto(destination, ref written))
                return false;
        return true;
    }

    public ChildCursor Children() => new(_document!, _handle);

    private bool TryFindAttribute(ushort nameId, out ReadOnlySpan<char> value)
    {
        value = default;
        if (nameId == ushort.MaxValue)
            return false;
        var node = Raw;
        if (node.PayloadIndex < 0)
            return false;
        var payload = _document!.GetPayload(node.PayloadIndex);
        for (var a = payload.FirstAttribute; a < payload.FirstAttribute + payload.AttributeCount; a++)
        {
            var attribute = _document.GetAttribute(a);
            if (attribute.NameId == nameId)
            {
                value = _document.GetValue(attribute.ValueStart, attribute.ValueLength);
                return true;
            }
        }
        return false;
    }

    private static bool ContainsToken(ReadOnlySpan<char> tokens, ReadOnlySpan<char> wanted)
    {
        var i = 0;
        while (i < tokens.Length)
        {
            while (i < tokens.Length && char.IsWhiteSpace(tokens[i]))
                i++;
            var start = i;
            while (i < tokens.Length && !char.IsWhiteSpace(tokens[i]))
                i++;
            if (i > start && tokens.Slice(start, i - start).SequenceEqual(wanted))
                return true;
        }
        return false;
    }

    /// <summary>Allocation-free enumerator over direct child nodes (first-child / next-sibling walk).</summary>
    public struct ChildCursor
    {
        private readonly CompactDocument _document;
        private readonly int _parent;
        private int _current;
        private bool _started;

        internal ChildCursor(CompactDocument document, int parent)
        {
            _document = document;
            _parent = parent;
            _current = -1;
            _started = false;
        }

        public readonly Node Current => new(_document, _current);

        public bool MoveNext()
        {
            if (!_started)
            {
                _started = true;
                _current = _document.GetNode(_parent).FirstChild;
            }
            else if (_current >= 0)
            {
                _current = _document.GetNode(_current).NextSibling;
            }
            return _current >= 0;
        }

        public readonly ChildCursor GetEnumerator() => this;
    }
}
