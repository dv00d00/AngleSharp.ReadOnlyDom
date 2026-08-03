using System.Buffers;

namespace AngleSharp.ReadOnlyDom.Streaming.Output;

/// <summary>
/// A reusable UTF-8 buffer whose publishable prefix can be consumed without moving its tentative suffix.
/// </summary>
public sealed class PublishableUtf8Buffer : IBufferWriter<byte>, IUtf8PublishSource
{
    private byte[] _buffer;
    private int _start;
    private int _publishableEnd;
    private int _end;

    public PublishableUtf8Buffer(int initialCapacity = 4 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        _buffer = new byte[initialCapacity];
    }

    /// <summary>Gets all buffered bytes which have not been consumed.</summary>
    public ReadOnlyMemory<byte> WrittenUtf8 => _buffer.AsMemory(_start, _end - _start);

    /// <inheritdoc />
    public ReadOnlyMemory<byte> PublishableUtf8 => _buffer.AsMemory(_start, _publishableEnd - _start);

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_end > _buffer.Length - count)
            throw new InvalidOperationException("Cannot advance past the available buffer.");
        _end += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_end);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_end);
    }

    /// <summary>Appends bytes to the tentative suffix.</summary>
    public void Write(ReadOnlySpan<byte> value)
    {
        value.CopyTo(GetSpan(value.Length));
        Advance(value.Length);
    }

    /// <summary>Marks every currently written byte as final and safe to publish downstream.</summary>
    public void MarkPublishable() => _publishableEnd = _end;

    /// <inheritdoc />
    public void AdvancePublished(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes > _publishableEnd - _start)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        _start += bytes;
        if (_start == _end)
            _start = _publishableEnd = _end = 0;
    }

    /// <summary>Discards all buffered bytes.</summary>
    public void Clear() => _start = _publishableEnd = _end = 0;

    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint < 0)
            throw new ArgumentOutOfRangeException(nameof(sizeHint));
        sizeHint = Math.Max(sizeHint, 1);
        if (sizeHint <= _buffer.Length - _end)
            return;

        var liveLength = _end - _start;
        if (sizeHint <= _buffer.Length - liveLength)
        {
            _buffer.AsSpan(_start, liveLength).CopyTo(_buffer);
            _publishableEnd -= _start;
            _end = liveLength;
            _start = 0;
            return;
        }

        var newLength = Math.Max(_buffer.Length * 2, checked(liveLength + sizeHint));
        var replacement = new byte[newLength];
        _buffer.AsSpan(_start, liveLength).CopyTo(replacement);
        _publishableEnd -= _start;
        _end = liveLength;
        _start = 0;
        _buffer = replacement;
    }
}
