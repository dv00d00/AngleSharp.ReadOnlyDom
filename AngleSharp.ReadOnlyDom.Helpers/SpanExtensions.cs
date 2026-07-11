using System.Buffers;

namespace AngleSharp.ReadOnlyDom.Helpers;

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
    public static StringSplitEnumeratorSearchValues Split(this ReadOnlySpan<char> span, SearchValues<char> sentinel) =>
        new(span, sentinel);

    /// <summary>
    /// Splits the span by the given sentinel, removing empty segments.
    /// </summary>
    /// <param name="span">The span to split</param>
    /// <param name="sentinel">The sentinel to split the span on.</param>
    /// <returns>An enumerator over the span segments.</returns>
    public static MemStringSplitEnumerator Split(this ReadOnlyMemory<char> span, ReadOnlySpan<char> sentinel) =>
        new(span, sentinel);

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
                    Current = _span[..index];
                    _span = _span[(index + 1)..];
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
                    Current = _mem[..index];
                    _mem = _mem[(index + 1)..];
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
                    Current = _span[..index];
                    _span = _span[(index + 1)..];
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
}