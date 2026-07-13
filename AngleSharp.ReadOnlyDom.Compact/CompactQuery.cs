namespace AngleSharp.ReadOnlyDom.Compact;

/// <summary>
/// Allocation-free queries over the compact columnar store.
/// </summary>
public static class CompactQuery
{
    public static Node Root(this CompactDocument document) => new(document, 0);

    /// <summary>Resolves a name once for ID-based predicates.</summary>
    public static ushort Name(this CompactDocument document, string name) => document.ResolveNameId(name);

    public static ushort Name(this CompactDocument document, ReadOnlySpan<char> name) => document.ResolveNameId(name);

    /// <summary>
    /// Scans every node below the document root in preorder.
    /// </summary>
    public static DescendantScan Descendants(this CompactDocument document) => new(document);

    /// <summary>Scans elements using the name-ID column.</summary>
    public static ElementQuery Elements(this CompactDocument document, string tag) =>
        new(document, document.ResolveNameId(tag), hasClass: false, default, null, hasAttr: false, default, null);

    public static ElementQuery Elements(this CompactDocument document, ReadOnlySpan<char> tag) =>
        new(document, document.ResolveNameId(tag), hasClass: false, default, null, hasAttr: false, default, null);

    public static ElementQuery Elements(this CompactDocument document, ushort tagId) =>
        new(document, tagId, hasClass: false, default, null, hasAttr: false, default, null);

    public struct DescendantScan
    {
        private readonly CompactDocument _document;
        private int _handle;

        internal DescendantScan(CompactDocument document)
        {
            _document = document;
            _handle = 0; // handle 0 is #document; MoveNext advances to the first real node
        }

        public readonly Node Current => new(_document, _handle);

        public bool MoveNext()
        {
            var next = _handle + 1;
            while (_document.TryGetContainingTemplateContentEnd(next, out var contentEnd))
                next = contentEnd;
            _handle = next;
            return _handle < _document.NodeCount;
        }

        public readonly DescendantScan GetEnumerator() => this;
    }

    public readonly struct ElementQuery
    {
        private readonly CompactDocument _document;
        private readonly ushort _tagId;
        private readonly bool _hasClass;
        private readonly ushort _classId;
        private readonly string? _classToken;
        private readonly bool _hasAttr;
        private readonly ushort _attrId;
        private readonly string? _attrValue;

        internal ElementQuery(
            CompactDocument document,
            ushort tagId,
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
            _hasClass = hasClass;
            _classId = classId;
            _classToken = classToken;
            _hasAttr = hasAttr;
            _attrId = attrId;
            _attrValue = attrValue;
        }

        /// <summary>Filters by a whitespace-separated class token.</summary>
        public ElementQuery WithClass(string token) =>
            new(_document, _tagId, true, _document.ResolveNameId("class"), token, _hasAttr, _attrId, _attrValue);

        /// <summary>Filters by attribute presence or value equality.</summary>
        public ElementQuery WithAttribute(string name, string? value = null) =>
            new(_document, _tagId, _hasClass, _classId, _classToken, true, _document.ResolveNameId(name), value);

        public Enumerator GetEnumerator() => new(this);

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

            var node = _document.GetNode(handle);
            if (node.PayloadIndex < 0)
                return false; // a filter is active but the element carries no attributes

            var payload = _document.GetPayload(node.PayloadIndex);
            var classOk = !_hasClass;
            var attrOk = !_hasAttr;
            for (var a = payload.FirstAttribute; a < payload.FirstAttribute + payload.AttributeCount; a++)
            {
                var attr = _document.GetAttribute(a);
                if (_hasClass && !classOk && attr.NameId == _classId)
                    classOk = HasToken(_document.GetValue(attr.ValueStart, attr.ValueLength), _classToken);
                if (_hasAttr && !attrOk && attr.NameId == _attrId)
                    attrOk = _attrValue is null || _document.GetValue(attr.ValueStart, attr.ValueLength).SequenceEqual(_attrValue);
                if (classOk && attrOk)
                    return true;
            }
            return classOk && attrOk;
        }

        private static bool HasToken(ReadOnlySpan<char> classes, ReadOnlySpan<char> wanted)
        {
            var i = 0;
            while (i < classes.Length)
            {
                while (i < classes.Length && char.IsWhiteSpace(classes[i]))
                    i++;
                var start = i;
                while (i < classes.Length && !char.IsWhiteSpace(classes[i]))
                    i++;
                if (i > start && classes.Slice(start, i - start).SequenceEqual(wanted))
                    return true;
            }
            return false;
        }

        public struct Enumerator
        {
            private readonly ElementQuery _query;
            private int _current;

            internal Enumerator(ElementQuery query)
            {
                _query = query;
                _current = -1;
            }

            public Node Current => new(_query._document, _current);

            public bool MoveNext()
            {
                if (_query._tagId == ushort.MaxValue)
                    return false;

                var document = _query._document;
                var handle = document.IndexOfName(_query._tagId, _current + 1);
                while (handle >= 0)
                {
                    if (document.TryGetContainingTemplateContentEnd(handle, out var contentEnd))
                    {
                        handle = document.IndexOfName(_query._tagId, contentEnd);
                        continue;
                    }
                    if (document.KindAt(handle) == CompactNodeKind.Element && _query.Matches(handle))
                    {
                        _current = handle;
                        return true;
                    }
                    handle = document.IndexOfName(_query._tagId, handle + 1);
                }
                return false;
            }
        }
    }
}
