using System.Buffers;

namespace AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;

/// <summary>
/// Streaming counterpart of <see cref="Utf8RewriteCollector"/>: observes the normalized input as the
/// tokenizer consumes it, applies start-tag edits the moment they are recorded, and publishes every
/// byte to the output as soon as it can no longer change. Between writes only the unpublishable
/// tail - at most the currently open start tag - stays buffered, so peak memory is independent of
/// document size.
/// </summary>
internal sealed class Utf8StreamingRewriteCollector : IStartTagEditCollector, IDisposable
{
    private readonly IBufferWriter<byte> _output;
    private readonly int _maximumHoldbackBytes;
    private byte[] _pending;
    private int _pendingStart;
    private int _pendingLength;
    private long _publishedThrough;
    private long _observedEnd;

    internal Utf8StreamingRewriteCollector(IBufferWriter<byte> output, HtmlStreamingLimits limits)
    {
        _output = output;
        _maximumHoldbackBytes = limits.EnforcesLimits ? limits.MaximumBufferedTokenBytes : int.MaxValue;
        _pending = ArrayPool<byte>.Shared.Rent(4096);
    }

    /// <summary>Receives each normalized input span before the tokenizer consumes it.</summary>
    internal void Observe(long sourceStart, ReadOnlySpan<byte> utf8)
    {
        if (sourceStart != _observedEnd)
            throw new InvalidOperationException("The observed input stream is not contiguous.");
        if (utf8.IsEmpty)
            return;

        EnsurePendingCapacity(utf8.Length);
        utf8.CopyTo(_pending.AsSpan(_pendingStart + _pendingLength));
        _pendingLength += utf8.Length;
        _observedEnd += utf8.Length;
    }

    public void AppendAttribute(
        long sourceStart,
        long sourceEnd,
        bool selfClosing,
        ReadOnlySpan<byte> name,
        ReadOnlySpan<byte> value
    )
    {
        Utf8RewriteCollector.ValidateName(name);
        if (
            sourceEnd > _observedEnd
            || sourceEnd <= sourceStart
            || sourceStart < 0
            || (sourceStart >= _publishedThrough && ByteAt(sourceStart) != (byte)'<')
        )
        {
            throw new InvalidOperationException("A recorded start-tag source range is outside the input.");
        }

        var position = sourceEnd - 1;
        if (position < _publishedThrough)
            throw new InvalidOperationException("Start-tag rewrite ranges are not ordered.");
        if (ByteAt(position) != (byte)'>')
            throw new InvalidOperationException("A recorded start-tag source range does not end at a tag close.");
        if (selfClosing && position > sourceStart && position > _publishedThrough && ByteAt(position - 1) == (byte)'/')
            position--;

        // Mirrors Utf8RewriteCollector.SegmentEnumerator: _publishedThrough plays the cursor.
        var needsSeparator =
            position == _publishedThrough
            || position == sourceStart
            || !Utf8RewriteCollector.IsHtmlSpace(ByteAt(position - 1));
        Publish(position);
        if (needsSeparator)
        {
            _output.GetSpan(1)[0] = (byte)' ';
            _output.Advance(1);
        }
        Utf8RewriteCollector.WriteAttributePayload(_output, name, value);
    }

    /// <summary>
    /// Publishes every observed byte below <paramref name="watermark"/> and enforces the holdback
    /// bound on what remains.
    /// </summary>
    internal void PublishUpTo(long watermark)
    {
        Publish(Math.Min(watermark, _observedEnd));
        if (_pendingLength > _maximumHoldbackBytes)
        {
            throw new HtmlStreamingLimitExceededException(
                HtmlStreamingLimit.BufferedTokenBytes,
                _maximumHoldbackBytes,
                _pendingLength
            );
        }
    }

    /// <summary>Publishes everything still pending; call after the tokenizer has completed.</summary>
    internal void Finish() => Publish(_observedEnd);

    public void Dispose()
    {
        var pending = _pending;
        _pending = [];
        _pendingStart = 0;
        _pendingLength = 0;
        if (pending.Length > 0)
            ArrayPool<byte>.Shared.Return(pending);
    }

    private byte ByteAt(long offset) => _pending[_pendingStart + (int)(offset - _publishedThrough)];

    private void Publish(long upTo)
    {
        var count = (int)(upTo - _publishedThrough);
        if (count <= 0)
            return;

        // Bounded slices keep pipe-style writers on pooled segments instead of one huge rent.
        var remaining = count;
        while (remaining > 0)
        {
            var slice = Math.Min(remaining, 32 * 1024);
            _pending.AsSpan(_pendingStart + (count - remaining), slice).CopyTo(_output.GetSpan(slice));
            _output.Advance(slice);
            remaining -= slice;
        }
        _pendingStart += count;
        _pendingLength -= count;
        _publishedThrough = upTo;
        if (_pendingLength == 0)
            _pendingStart = 0;
    }

    private void EnsurePendingCapacity(int incoming)
    {
        var required = _pendingLength + incoming;
        if (_pendingStart + required <= _pending.Length)
            return;

        if (required <= _pending.Length)
        {
            Array.Copy(_pending, _pendingStart, _pending, 0, _pendingLength);
            _pendingStart = 0;
            return;
        }

        var grown = ArrayPool<byte>.Shared.Rent(required);
        Array.Copy(_pending, _pendingStart, grown, 0, _pendingLength);
        ArrayPool<byte>.Shared.Return(_pending);
        _pending = grown;
        _pendingStart = 0;
    }
}
