namespace AngleSharp.ReadOnlyDom.CompactPrototype;

/// <summary>
/// Predicate-pushdown-lite query helpers over the columnar store.
///
/// The tag predicate is the selective one, so it is "pushed down" into a vectorized scan of the
/// contiguous name-id column (<see cref="CompactDocument.IndexOfName"/>). Cheaper-to-check secondary
/// predicates (a class token, one attribute presence/equality) are applied only to the candidates the
/// scan surfaces — so attribute values are read for matches, not for every node.
///
/// Enumeration is a struct enumerator yielding node handles; there are no per-node allocations and no
/// delegate predicates. An object-graph DOM cannot push a tag filter into a SIMD scan like this because
/// its names live behind per-node reference indirection.
/// </summary>
public static class CompactQuery
{
    public static ElementQuery Elements(this CompactDocument document, string tag) =>
        new(document, document.FindNameId(tag), hasClass: false, default, null, hasAttr: false, default, null);

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

        /// <summary>Adds a <c>class</c>-token filter (whitespace-separated match), applied to candidates.</summary>
        public ElementQuery WithClass(string token) =>
            new(_document, _tagId, true, _document.FindNameId("class"), token, _hasAttr, _attrId, _attrValue);

        /// <summary>Adds an attribute filter: presence when <paramref name="value"/> is null, else equality.</summary>
        public ElementQuery WithAttribute(string name, string? value = null) =>
            new(_document, _tagId, _hasClass, _classId, _classToken, true, _document.FindNameId(name), value);

        public Enumerator GetEnumerator() => new(this);

        public int Count()
        {
            var count = 0;
            var enumerator = GetEnumerator();
            while (enumerator.MoveNext())
                count++;
            return count;
        }

        public int First()
        {
            var enumerator = GetEnumerator();
            return enumerator.MoveNext() ? enumerator.Current : -1;
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

            public int Current => _current;

            public bool MoveNext()
            {
                if (_query._tagId == ushort.MaxValue)
                    return false;

                var document = _query._document;
                var handle = document.IndexOfName(_query._tagId, _current + 1);
                while (handle >= 0)
                {
                    if (_query.Matches(handle))
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
