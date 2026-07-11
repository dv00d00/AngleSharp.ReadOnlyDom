using System.Buffers;

namespace AngleSharp.ReadOnlyDom;

internal static class SpanExtensions
{
    /// <summary>
    /// Splits the span by the given sentinel, removing empty segments.
    /// </summary>
    /// <param name="span">The span to split</param>
    /// <param name="sentinel">The sentinel to split the span on.</param>
    /// <returns>An enumerator over the span segments.</returns>
    public static StringSplitEnumerator Split(this ReadOnlySpan<char> span, ReadOnlySpan<char> sentinel) =>
        new(span, sentinel);

    /// <summary>
    /// Splits the span by the given sentinel, removing empty segments.
    /// </summary>
    /// <param name="span">The span to split</param>
    /// <param name="sentinel">The sentinel to split the span on.</param>
    /// <returns>An enumerator over the span segments.</returns>
    public static MemStringSplitEnumerator Split(this ReadOnlyMemory<char> span, ReadOnlySpan<char> sentinel) =>
        new(span, sentinel);

#if NETSTANDARD2_0
    /// <summary>
    /// Splits the span on any of the given characters, removing empty segments.
    /// netstandard2.0 fallback for platforms without <c>SearchValues&lt;char&gt;</c>.
    /// </summary>
    /// <param name="span">The span to split</param>
    /// <param name="anyOf">The set of characters to split the span on.</param>
    /// <returns>An enumerator over the span segments.</returns>
    public static StringSplitEnumeratorAnyChars SplitAny(this ReadOnlySpan<char> span, ReadOnlySpan<char> anyOf) =>
        new(span, anyOf);
#else
    /// <summary>
    /// Splits the span by the given sentinel, removing empty segments.
    /// </summary>
    /// <param name="span">The span to split</param>
    /// <param name="sentinel">The sentinel to split the span on.</param>
    /// <returns>An enumerator over the span segments.</returns>
    public static StringSplitEnumeratorSearchValues Split(this ReadOnlySpan<char> span, SearchValues<char> sentinel) =>
        new(span, sentinel);
#endif

    internal ref struct StringSplitEnumerator(ReadOnlySpan<char> span, ReadOnlySpan<char> sentinel)
    {
        private readonly ReadOnlySpan<char> _sentinel = sentinel;
        private ReadOnlySpan<char> _span = span;

        public bool MoveNext()
        {
            while (true)
            {
                if (_span.Length == 0)
                {
                    return false;
                }

                var index = _span.IndexOf(_sentinel, StringComparison.Ordinal);
                if (index < 0)
                {
                    Current = _span;
                    _span = default;
                }
                else
                {
                    Current = _span.Slice(0, index);
                    _span = _span.Slice(index + 1);
                }

                if (Current.Length == 0)
                {
                    continue;
                }

                return true;
            }
        }

        public ReadOnlySpan<char> Current { get; private set; }

        public readonly StringSplitEnumerator GetEnumerator() => this;
    }

    internal ref struct MemStringSplitEnumerator(ReadOnlyMemory<char> mem, ReadOnlySpan<char> sentinel)
    {
        private readonly ReadOnlySpan<char> _sentinel = sentinel;
        private ReadOnlyMemory<char> _mem = mem;

        public bool MoveNext()
        {
            while (true)
            {
                if (_mem.Length == 0)
                {
                    return false;
                }

                var index = _mem.Span.IndexOf(_sentinel, StringComparison.Ordinal);
                if (index < 0)
                {
                    Current = _mem;
                    _mem = default;
                }
                else
                {
                    Current = _mem.Slice(0, index);
                    _mem = _mem.Slice(index + 1);
                }

                if (Current.Length == 0)
                {
                    continue;
                }

                return true;
            }
        }

        public ReadOnlyMemory<char> Current { get; private set; }

        public readonly MemStringSplitEnumerator GetEnumerator() => this;
    }

#if NETSTANDARD2_0
    internal ref struct StringSplitEnumeratorAnyChars(ReadOnlySpan<char> span, ReadOnlySpan<char> anyOf)
    {
        private readonly ReadOnlySpan<char> _anyOf = anyOf;
        private ReadOnlySpan<char> _span = span;

        public bool MoveNext()
        {
            while (true)
            {
                if (_span.Length == 0)
                {
                    return false;
                }

                var index = IndexOfAny(_span, _anyOf);
                if (index < 0)
                {
                    Current = _span;
                    _span = default;
                }
                else
                {
                    Current = _span.Slice(0, index);
                    _span = _span.Slice(index + 1);
                }

                if (Current.Length == 0)
                {
                    continue;
                }

                return true;
            }
        }

        private static int IndexOfAny(ReadOnlySpan<char> span, ReadOnlySpan<char> anyOf)
        {
            for (var i = 0; i < span.Length; i++)
            {
                for (var j = 0; j < anyOf.Length; j++)
                {
                    if (span[i] == anyOf[j])
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        public ReadOnlySpan<char> Current { get; private set; }

        public readonly StringSplitEnumeratorAnyChars GetEnumerator() => this;
    }
#else
    internal ref struct StringSplitEnumeratorSearchValues(ReadOnlySpan<char> span, SearchValues<char> sentinel)
    {
        private ReadOnlySpan<char> _span = span;

        public bool MoveNext()
        {
            while (true)
            {
                if (_span.Length == 0)
                {
                    return false;
                }

                var index = _span.IndexOfAny(sentinel);
                if (index < 0)
                {
                    Current = _span;
                    _span = default;
                }
                else
                {
                    Current = _span.Slice(0, index);
                    _span = _span.Slice(index + 1);
                }

                if (Current.Length == 0)
                {
                    continue;
                }

                return true;
            }
        }

        public ReadOnlySpan<char> Current { get; private set; }

        public readonly StringSplitEnumeratorSearchValues GetEnumerator() => this;
    }
#endif
}