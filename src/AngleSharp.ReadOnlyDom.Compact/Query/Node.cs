using System.Buffers;
using System.Text;
using AngleSharp.ReadOnlyDom.Compact.Document;

namespace AngleSharp.ReadOnlyDom.Compact.Query;

/// <summary>
///     A value-type cursor over one node in a <see cref="CompactDocument" />.
/// </summary>
public readonly struct Node
{
    private readonly CompactDocument? _document;

    internal Node(CompactDocument document, int handle)
    {
        _document = document;
        Handle = handle;
    }

    public bool Exists => _document is not null && Handle >= 0;
    internal int Handle { get; }

    internal CompactDocument Document => _document!;

    private CompactNode Raw => _document!.GetNode(Handle);

    public CompactNodeKind Kind => _document!.KindAt(Handle);
    public bool IsElement => _document!.KindAt(Handle) == CompactNodeKind.Element;
    internal ushort NameId => _document!.NameIdAt(Handle);
    public string Name => _document!.GetName(_document.NameIdAt(Handle));

    public ReadOnlySpan<char> LocalName
    {
        get
        {
            var name = Name.AsSpan();
            var separator = name.IndexOf(':');
            return separator < 0 ? name : name[(separator + 1)..];
        }
    }

    public bool Is(string tag)
    {
        return Is(_document!.ResolveNameId(tag));
    }

    public bool Is(ReadOnlySpan<char> tag)
    {
        return Is(_document!.ResolveNameId(tag));
    }

    internal bool Is(ushort tagId)
    {
        return tagId != ushort.MaxValue
            && _document!.KindAt(Handle) == CompactNodeKind.Element
            && _document.NameIdAt(Handle) == tagId;
    }

    /// <summary>The parent node, or a non-existent cursor when parent links were not retained.</summary>
    public Node Parent =>
        _document is not null && _document.HasParentLinks ? new Node(_document, _document.GetParent(Handle)) : default;

    public bool IsDescendantOf(Node ancestor)
    {
        return _document is not null
            && ReferenceEquals(_document, ancestor._document)
            && _document.IsInSameTreeScope(Handle, ancestor.Handle)
            && Handle > ancestor.Handle
            && Handle < ancestor.Raw.SubtreeEndExclusive;
    }

    public bool TryGetSourceLocation(out CompactSourceLocation source)
    {
        if (_document is not null)
            return _document.TryGetSourceLocation(Handle, out source);
        source = default;
        return false;
    }

    /// <summary>The attribute value (empty span if absent — use <see cref="HasAttr(string)" /> to disambiguate).</summary>
    public ReadOnlySpan<char> Attr(string name)
    {
        return Attr(name.AsSpan());
    }

    public ReadOnlySpan<char> Attr(ReadOnlySpan<char> name)
    {
        return Attr(_document!.ResolveNameId(name));
    }

    internal ReadOnlySpan<char> Attr(ushort nameId)
    {
        return TryFindAttribute(nameId, out var value) ? value : default;
    }

    public bool HasAttr(string name)
    {
        return HasAttr(name.AsSpan());
    }

    public bool HasAttr(ReadOnlySpan<char> name)
    {
        return HasAttr(_document!.ResolveNameId(name));
    }

    /// <summary>Checks an attribute using a previously resolved name ID.</summary>
    internal bool HasAttr(ushort nameId)
    {
        return TryFindAttribute(nameId, out _);
    }

    public bool HasClass(string token)
    {
        HtmlClassToken.Validate(token, nameof(token));
        return HasClass(_document!.ResolveNameId("class"), token);
    }

    public bool HasClass(ReadOnlySpan<char> token)
    {
        HtmlClassToken.Validate(token, nameof(token));
        return HasClass(_document!.ResolveNameId("class"), token);
    }

    /// <summary>Checks a class token using a previously resolved <c>class</c> attribute name ID.</summary>
    internal bool HasClass(ushort classNameId, ReadOnlySpan<char> token)
    {
        if (!TryFindAttribute(classNameId, out var classes))
            return false;
        return HtmlClassToken.Contains(classes, token);
    }

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
        var document = _document!;
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
        var document = _document!;
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

    public ChildCursor Children()
    {
        return new ChildCursor(
            _document!,
            _document!.IsTemplate(Handle) ? -1 : Raw.FirstChild,
            Raw.SubtreeEndExclusive
        );
    }

    public ChildCursor TemplateContent()
    {
        return new ChildCursor(
            _document!,
            _document!.TryGetTemplateContent(Handle, out var contentStart) ? contentStart : -1,
            Raw.SubtreeEndExclusive
        );
    }

    private bool TryFindAttribute(ushort nameId, out ReadOnlySpan<char> value)
    {
        value = default;
        if (nameId == ushort.MaxValue)
            return false;
        var document = _document!;
        if (!document.TryGetAttributeRange(Handle, out var first, out var count))
            return false;
        for (var a = first; a < first + count; a++)
            if (document.AttributeNameIdAt(a) == nameId)
            {
                value = document.AttributeValueSpanAt(a);
                return true;
            }

        return false;
    }

    private struct LengthSink : ISpanSink
    {
        internal int Length;

        public void Append(ReadOnlySpan<char> value)
        {
            Length += value.Length;
        }
    }

    public struct ChildCursor
    {
        private readonly CompactDocument _document;
        private readonly int _first;
        private readonly int _endExclusive;
        private int _current;
        private bool _started;

        internal ChildCursor(CompactDocument document, int first, int endExclusive)
        {
            _document = document;
            _first = first;
            _endExclusive = endExclusive;
            _current = -1;
            _started = false;
        }

        public readonly Node Current => new(_document, _current);

        public bool MoveNext()
        {
            if (!_started)
            {
                _started = true;
                _current = _first;
            }
            else if (_current >= 0)
            {
                _current = _document.GetNode(_current).SubtreeEndExclusive;
            }

            return _current >= 0 && _current < _endExclusive;
        }

        public readonly ChildCursor GetEnumerator()
        {
            return this;
        }
    }
}
