using AngleSharp.ReadOnlyDom.Compact.Document;

namespace AngleSharp.ReadOnlyDom.Compact.Query;

/// <summary>
///     A value-type cursor over one node in a <see cref="CompactDocument" />.
/// </summary>
public readonly partial struct Node
{
    private readonly CompactDocument? _document;

    internal Node(CompactDocument document, int handle)
    {
        _document = document;
        Handle = handle;
    }

    public bool Exists
    {
        get
        {
            if (_document is null)
                return false;
            _document.ThrowIfDisposed();
            return Handle >= 0;
        }
    }
    internal int Handle { get; }

    internal CompactDocument Document
    {
        get
        {
            var document = _document!;
            document.ThrowIfDisposed();
            return document;
        }
    }

    private CompactNode Raw => Document.GetNode(Handle);

    public CompactNodeKind Kind => Document.KindAt(Handle);
    public bool IsElement => Document.KindAt(Handle) == CompactNodeKind.Element;
    public string Name
    {
        get
        {
            var document = Document;
            return document.GetName(document.NameIdAt(Handle));
        }
    }

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
        return Is(Document.ResolveNameId(tag));
    }

    public bool Is(ReadOnlySpan<char> tag)
    {
        return Is(Document.ResolveNameId(tag));
    }

    internal bool Is(ushort tagId)
    {
        return tagId != ushort.MaxValue
            && _document!.KindAt(Handle) == CompactNodeKind.Element
            && _document.NameIdAt(Handle) == tagId;
    }

    /// <summary>The parent node, or a non-existent cursor when parent links were not retained.</summary>
    public Node Parent
    {
        get
        {
            if (_document is null)
                return default;
            _document.ThrowIfDisposed();
            return _document.HasParentLinks ? new Node(_document, _document.GetParent(Handle)) : default;
        }
    }

    public bool IsDescendantOf(Node ancestor)
    {
        if (_document is null)
            return false;
        _document.ThrowIfDisposed();
        if (ancestor._document is not null)
            ancestor._document.ThrowIfDisposed();
        return ReferenceEquals(_document, ancestor._document)
            && _document.IsInSameTreeScope(Handle, ancestor.Handle)
            && Handle > ancestor.Handle
            && Handle < ancestor.Raw.SubtreeEndExclusive;
    }

    public bool TryGetSourceLocation(out CompactSourceLocation source)
    {
        if (_document is not null)
        {
            _document.ThrowIfDisposed();
            return _document.TryGetSourceLocation(Handle, out source);
        }
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
        return Attr(Document.ResolveNameId(name));
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
        return HasAttr(Document.ResolveNameId(name));
    }

    /// <summary>Checks an attribute using a previously resolved name ID.</summary>
    internal bool HasAttr(ushort nameId)
    {
        return TryFindAttribute(nameId, out _);
    }

    public bool HasClass(string token)
    {
        var document = Document;
        HtmlClassToken.Validate(token, nameof(token));
        return HasClass(document.ResolveNameId("class"), token);
    }

    public bool HasClass(ReadOnlySpan<char> token)
    {
        var document = Document;
        HtmlClassToken.Validate(token, nameof(token));
        return HasClass(document.ResolveNameId("class"), token);
    }

    /// <summary>Checks a class token using a previously resolved <c>class</c> attribute name ID.</summary>
    internal bool HasClass(ushort classNameId, ReadOnlySpan<char> token)
    {
        if (!TryFindAttribute(classNameId, out var classes))
            return false;
        return HtmlClassToken.Contains(classes, token);
    }

    public ChildCursor Children()
    {
        var document = Document;
        var raw = document.GetNode(Handle);
        return new ChildCursor(document, document.IsTemplate(Handle) ? -1 : raw.FirstChild, raw.SubtreeEndExclusive);
    }

    public ChildCursor TemplateContent()
    {
        var document = Document;
        var raw = document.GetNode(Handle);
        return new ChildCursor(
            document,
            document.TryGetTemplateContent(Handle, out var contentStart) ? contentStart : -1,
            raw.SubtreeEndExclusive
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

        public readonly Node Current
        {
            get
            {
                _document.ThrowIfDisposed();
                return new Node(_document, _current);
            }
        }

        public bool MoveNext()
        {
            _document.ThrowIfDisposed();
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
            _document.ThrowIfDisposed();
            return this;
        }
    }
}
