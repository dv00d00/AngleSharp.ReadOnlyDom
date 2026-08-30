using System.Runtime.CompilerServices;

namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

/// <summary>
/// Append-only byte buffer for in-flight token text.
/// </summary>
/// <remarks>
/// The tokenizer previously used <see cref="System.Buffers.ArrayBufferWriter{T}" />, whose
/// <c>GetSpan</c>/<c>Advance</c>/<c>Write</c> surface the JIT does not inline: the tier-1 body of
/// the scan loop carried four real calls into it, one per tag name, attribute name and attribute
/// value append. Appends here are a bounds check plus a copy, and the grow path is kept in a
/// separate non-inlined method so the fast path stays small enough to inline into the loop.
/// </remarks>
internal sealed class Utf8TokenBuffer(Int32 capacity)
{
    private Byte[] _array = new Byte[capacity];
    private Int32 _count;

    public Int32 WrittenCount => _count;

    public ReadOnlySpan<Byte> WrittenSpan => new(_array, 0, _count);

    public void ResetWrittenCount() => _count = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(Byte value)
    {
        var array = _array;
        var count = _count;
        if ((UInt32)count < (UInt32)array.Length)
        {
            array[count] = value;
            _count = count + 1;
            return;
        }
        AppendWithGrowth(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(ReadOnlySpan<Byte> value)
    {
        var array = _array;
        var count = _count;
        if (value.Length <= array.Length - count)
        {
            value.CopyTo(new Span<Byte>(array, count, value.Length));
            _count = count + value.Length;
            return;
        }
        AppendWithGrowth(value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AppendWithGrowth(Byte value)
    {
        Grow(1);
        _array[_count++] = value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AppendWithGrowth(ReadOnlySpan<Byte> value)
    {
        Grow(value.Length);
        value.CopyTo(new Span<Byte>(_array, _count, value.Length));
        _count += value.Length;
    }

    private void Grow(Int32 additional)
    {
        var required = _count + additional;
        var capacity = Math.Max(_array.Length == 0 ? 32 : _array.Length * 2, required);
        Array.Resize(ref _array, capacity);
    }
}
