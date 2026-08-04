using AngleSharp.ReadOnlyDom.Compact.Document;

namespace AngleSharp.ReadOnlyDom.Compact.Query;

/// <summary>
///     Allocation-free queries over the compact columnar store.
/// </summary>
public static class CompactQuery
{
    public static Node Root(this CompactDocument document)
    {
        document.ThrowIfDisposed();
        return new Node(document, 0);
    }

    /// <summary>Resolves a name once for ID-based predicates.</summary>
    internal static ushort Name(this CompactDocument document, string name)
    {
        return document.ResolveNameId(name);
    }

    internal static ushort Name(this CompactDocument document, ReadOnlySpan<char> name)
    {
        return document.ResolveNameId(name);
    }

    /// <summary>
    ///     Scans every node below the document root in preorder.
    /// </summary>
    public static DescendantScan Descendants(this CompactDocument document)
    {
        document.ThrowIfDisposed();
        return new DescendantScan(document);
    }

    public static DescendantScan Descendants(this Node node)
    {
        return new DescendantScan(node);
    }

    /// <summary>Scans elements using the name-ID column.</summary>
    public static ElementQuery Elements(this CompactDocument document, string tag)
    {
        document.ThrowIfDisposed();
        return new ElementQuery(
            document,
            document.ResolveNameId(tag),
            0,
            document.RawNodeCount,
            false,
            default,
            null,
            false,
            default,
            null
        );
    }

    public static ElementQuery Elements(this CompactDocument document, ReadOnlySpan<char> tag)
    {
        document.ThrowIfDisposed();
        return new ElementQuery(
            document,
            document.ResolveNameId(tag),
            0,
            document.RawNodeCount,
            false,
            default,
            null,
            false,
            default,
            null
        );
    }

    internal static ElementQuery Elements(this CompactDocument document, ushort tagId)
    {
        return new ElementQuery(document, tagId, 0, document.RawNodeCount, false, default, null, false, default, null);
    }

    public static ElementQuery Elements(this Node node, string tag)
    {
        return CreateElements(node, node.Document.ResolveNameId(tag));
    }

    public static ElementQuery Elements(this Node node, ReadOnlySpan<char> tag)
    {
        return CreateElements(node, node.Document.ResolveNameId(tag));
    }

    internal static ElementQuery Elements(this Node node, ushort tagId)
    {
        return CreateElements(node, tagId);
    }

    private static ElementQuery CreateElements(Node node, ushort tagId)
    {
        var document = node.Document;
        var start = node.Handle + 1;
        var end = document.IsTemplate(node.Handle) ? start : document.GetNode(node.Handle).SubtreeEndExclusive;
        return new ElementQuery(document, tagId, start, end, false, default, null, false, default, null);
    }

    public struct DescendantScan
    {
        private readonly CompactDocument _document;
        private int _handle;
        private readonly int _endExclusive;

        internal DescendantScan(CompactDocument document)
        {
            _document = document;
            _handle = 0; // handle 0 is #document; MoveNext advances to the first real node
            _endExclusive = document.RawNodeCount;
        }

        internal DescendantScan(Node node)
        {
            _document = node.Document;
            _handle = node.Handle;
            _endExclusive = _document.IsTemplate(node.Handle)
                ? node.Handle + 1
                : _document.GetNode(node.Handle).SubtreeEndExclusive;
        }

        public readonly Node Current
        {
            get
            {
                _document.ThrowIfDisposed();
                return new Node(_document, _handle);
            }
        }

        public bool MoveNext()
        {
            _document.ThrowIfDisposed();
            var next = _handle + 1;
            while (_document.TryGetContainingTemplateContentEnd(next, out var contentEnd))
                next = contentEnd;
            _handle = next;
            return _handle < _endExclusive;
        }

        public readonly DescendantScan GetEnumerator()
        {
            _document.ThrowIfDisposed();
            return this;
        }
    }

    public readonly struct ElementQuery
    {
        private readonly CompactDocument _document;
        private readonly ushort _tagId;
        private readonly int _start;
        private readonly int _endExclusive;
        private readonly bool _hasClass;
        private readonly ushort _classId;
        private readonly string? _classToken;
        private readonly bool _hasAttr;
        private readonly ushort _attrId;
        private readonly string? _attrValue;

        internal ElementQuery(
            CompactDocument document,
            ushort tagId,
            int start,
            int endExclusive,
            bool hasClass,
            ushort classId,
            string? classToken,
            bool hasAttr,
            ushort attrId,
            string? attrValue
        )
        {
            _document = document;
            _tagId = tagId;
            _start = start;
            _endExclusive = endExclusive;
            _hasClass = hasClass;
            _classId = classId;
            _classToken = classToken;
            _hasAttr = hasAttr;
            _attrId = attrId;
            _attrValue = attrValue;
        }

        /// <summary>Filters by a whitespace-separated class token.</summary>
        public ElementQuery WithClass(string token)
        {
            _document.ThrowIfDisposed();
            HtmlClassToken.Validate(token, nameof(token));
            return WithClass(_document.ResolveNameId("class"), token);
        }

        /// <summary>Filters by a class token using a previously resolved <c>class</c> attribute name ID.</summary>
        internal ElementQuery WithClass(ushort classNameId, string token)
        {
            return new ElementQuery(
                _document,
                _tagId,
                _start,
                _endExclusive,
                true,
                classNameId,
                token,
                _hasAttr,
                _attrId,
                _attrValue
            );
        }

        /// <summary>Filters by attribute presence or value equality.</summary>
        public ElementQuery WithAttribute(string name, string? value = null)
        {
            _document.ThrowIfDisposed();
            return WithAttribute(_document.ResolveNameId(name), value);
        }

        /// <summary>Filters by a previously resolved attribute name ID.</summary>
        internal ElementQuery WithAttribute(ushort nameId, string? value = null)
        {
            return new ElementQuery(
                _document,
                _tagId,
                _start,
                _endExclusive,
                _hasClass,
                _classId,
                _classToken,
                true,
                nameId,
                value
            );
        }

        public Enumerator GetEnumerator()
        {
            _document.ThrowIfDisposed();
            return new Enumerator(this);
        }

        public int Count()
        {
            var count = 0;
            var enumerator = GetEnumerator();
            while (enumerator.MoveNext())
                count++;
            return count;
        }

        public Node First()
        {
            var enumerator = GetEnumerator();
            return enumerator.MoveNext() ? enumerator.Current : default;
        }

        private bool Matches(int handle)
        {
            if (!_hasClass && !_hasAttr)
                return true;

            // a filter is active but the element carries no attributes
            if (!_document.TryGetAttributeRange(handle, out var first, out var count))
                return false;

            var classOk = !_hasClass;
            var attrOk = !_hasAttr;
            for (var a = first; a < first + count; a++)
            {
                var nameId = _document.AttributeNameIdAt(a);
                if (_hasClass && !classOk && nameId == _classId)
                    classOk = HtmlClassToken.Contains(_document.AttributeValueSpanAt(a), _classToken);
                if (_hasAttr && !attrOk && nameId == _attrId)
                    attrOk = _attrValue is null || _document.AttributeValueSpanAt(a).SequenceEqual(_attrValue);
                if (classOk && attrOk)
                    return true;
            }

            return classOk && attrOk;
        }

        public struct Enumerator
        {
            private readonly ElementQuery _query;
            private int _current;

            internal Enumerator(ElementQuery query)
            {
                _query = query;
                _current = query._start - 1;
            }

            public Node Current
            {
                get
                {
                    _query._document.ThrowIfDisposed();
                    return new Node(_query._document, _current);
                }
            }

            public bool MoveNext()
            {
                _query._document.ThrowIfDisposed();
                if (_query._tagId == ushort.MaxValue)
                    return false;

                var document = _query._document;
                var handle = document.IndexOfName(_query._tagId, _current + 1, _query._endExclusive);
                while (handle >= 0)
                {
                    if (document.TryGetContainingTemplateContentEnd(handle, out var contentEnd))
                    {
                        handle = document.IndexOfName(_query._tagId, contentEnd, _query._endExclusive);
                        continue;
                    }

                    if (document.KindAt(handle) == CompactNodeKind.Element && _query.Matches(handle))
                    {
                        _current = handle;
                        return true;
                    }

                    handle = document.IndexOfName(_query._tagId, handle + 1, _query._endExclusive);
                }

                return false;
            }
        }
    }
}
