namespace AngleSharp.ReadOnlyDom.Helpers;

internal static class SpanExtensions
{
    /// <summary>
    /// Splits the span by the given sentinel, removing empty segments.
    /// </summary>
    /// <param name="span">The span to split</param>
    /// <param name="sentinel">The sentinel to split the span on.</param>
    /// <returns>An enumerator over the span segments.</returns>
    public static StringSplitEnumerator Split(this ReadOnlySpan<Char> span, ReadOnlySpan<Char> sentinel) =>
        new(span, sentinel);

    /// <summary>
    /// Splits the span by the given sentinel, removing empty segments.
    /// </summary>
    /// <param name="span">The span to split</param>
    /// <param name="sentinel">The sentinel to split the span on.</param>
    /// <returns>An enumerator over the span segments.</returns>
    public static MemStringSplitEnumerator Split(this ReadOnlyMemory<Char> span, ReadOnlySpan<Char> sentinel) =>
        new(span, sentinel);

    internal ref struct StringSplitEnumerator
    {
        private readonly ReadOnlySpan<Char> _sentinel;
        private ReadOnlySpan<Char> _span;

        public StringSplitEnumerator(ReadOnlySpan<Char> span, ReadOnlySpan<Char> sentinel)
        {
            _span = span;
            _sentinel = sentinel;
        }

        public Boolean MoveNext()
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

        public ReadOnlySpan<Char> Current { get; private set; }

        public readonly StringSplitEnumerator GetEnumerator() => this;
    }

    internal ref struct MemStringSplitEnumerator
    {
        private readonly ReadOnlySpan<Char> _sentinel;
        private ReadOnlyMemory<Char> _mem;

        public MemStringSplitEnumerator(ReadOnlyMemory<Char> mem, ReadOnlySpan<Char> sentinel)
        {
            _mem = mem;
            _sentinel = sentinel;
        }

        public Boolean MoveNext()
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

        public ReadOnlyMemory<Char> Current { get; private set; }

        public readonly MemStringSplitEnumerator GetEnumerator() => this;
    }
}