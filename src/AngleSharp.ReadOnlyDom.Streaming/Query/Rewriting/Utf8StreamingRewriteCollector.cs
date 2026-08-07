using System.Buffers;

namespace AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;

/// <summary>
/// Streaming counterpart of <see cref="Utf8RewriteCollector"/>. Edits recorded during a tokenizer
/// write only capture payload bytes; when the write completes, <see cref="PublishWindow"/> receives
/// the consumed span while it is still addressable and publishes everything below the tokenizer's
/// rewrite watermark straight from that borrowed span - untouched bytes cross exactly once, into
/// the output writer. Only the unpublishable tail (at most the currently open start tag) is copied
/// into the pending holdback buffer, so peak memory is independent of document size.
/// </summary>
internal sealed class Utf8StreamingRewriteCollector : IStartTagEditCollector, IDisposable
{
    private readonly IBufferWriter<byte>? _output;
    private readonly StreamingRewriteSegmentSink? _sink;
    private readonly int _maximumHoldbackBytes;
    private readonly ArrayBufferWriter<byte> _payload = new(256);
    private readonly List<Insertion> _insertions = [];
    private byte[] _pending;
    private int _pendingLength;
    private long _publishedThrough;
    private long _observedEnd;

    internal Utf8StreamingRewriteCollector(IBufferWriter<byte> output, HtmlStreamingLimits limits)
        : this(limits)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    internal Utf8StreamingRewriteCollector(StreamingRewriteSegmentSink sink, HtmlStreamingLimits limits)
        : this(limits)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    private Utf8StreamingRewriteCollector(HtmlStreamingLimits limits)
    {
        _maximumHoldbackBytes = limits.EnforcesLimits ? limits.MaximumBufferedTokenBytes : int.MaxValue;
        _pending = ArrayPool<byte>.Shared.Rent(4096);
    }

    /// <summary>
    /// Records the edit; source bytes are validated and interleaved later, in
    /// <see cref="PublishWindow"/>, where they are addressable.
    /// </summary>
    public void AppendAttribute(
        long sourceStart,
        long sourceEnd,
        bool selfClosing,
        ReadOnlySpan<byte> name,
        ReadOnlySpan<byte> value
    )
    {
        Utf8RewriteCollector.ValidateName(name);
        var payloadStart = _payload.WrittenCount;
        Utf8RewriteCollector.WriteAttributePayload(_payload, name, value);
        _insertions.Add(
            new Insertion(sourceStart, sourceEnd, selfClosing, payloadStart, _payload.WrittenCount - payloadStart)
        );
    }

    /// <summary>
    /// Consumes one tokenizer write window: the pending holdback covers
    /// [<see cref="_publishedThrough"/>, chunkStart) and <paramref name="chunk"/> covers
    /// [chunkStart, chunkStart + length). Applies the recorded edits, publishes everything below
    /// <paramref name="watermark"/>, and carries the remainder into the holdback buffer.
    /// </summary>
    internal void PublishWindow(long chunkStart, ReadOnlySpan<byte> chunk, long watermark)
    {
        if (chunkStart != _observedEnd)
            throw new InvalidOperationException("The observed input stream is not contiguous.");
        _observedEnd += chunk.Length;

        var pendingBase = chunkStart - _pendingLength;
        var limit = Math.Min(watermark, _observedEnd);

        foreach (var insertion in _insertions)
        {
            ApplyInsertion(insertion, pendingBase, chunkStart, chunk, limit);
        }
        _insertions.Clear();
        _payload.ResetWrittenCount();

        PublishRange(_publishedThrough, limit, pendingBase, chunkStart, chunk);
        _publishedThrough = limit;

        // Carry [limit, observedEnd) - at most the open start tag - into the holdback buffer.
        var pendingCarry = (int)(
            Math.Max(limit, pendingBase) < chunkStart ? chunkStart - Math.Max(limit, pendingBase) : 0
        );
        var chunkCarryStart = (int)(Math.Max(limit, chunkStart) - chunkStart);
        var chunkCarry = chunk.Length - chunkCarryStart;
        var carry = pendingCarry + chunkCarry;
        if (carry > _maximumHoldbackBytes)
        {
            throw new HtmlStreamingLimitExceededException(
                HtmlStreamingLimit.BufferedTokenBytes,
                _maximumHoldbackBytes,
                carry
            );
        }
        EnsurePendingCapacity(carry);
        if (pendingCarry > 0)
        {
            // The surviving holdback tail is the suffix of the previous holdback; shift it home.
            Array.Copy(_pending, (int)(Math.Max(limit, pendingBase) - pendingBase), _pending, 0, pendingCarry);
        }
        if (chunkCarry > 0)
        {
            chunk[chunkCarryStart..].CopyTo(_pending.AsSpan(pendingCarry));
        }
        _pendingLength = carry;
    }

    /// <summary>Publishes everything still pending; call after the tokenizer has completed.</summary>
    internal void Finish() => PublishWindow(_observedEnd, [], _observedEnd);

    public void Dispose()
    {
        var pending = _pending;
        _pending = [];
        _pendingLength = 0;
        if (pending.Length > 0)
            ArrayPool<byte>.Shared.Return(pending);
    }

    private void ApplyInsertion(
        in Insertion insertion,
        long pendingBase,
        long chunkStart,
        ReadOnlySpan<byte> chunk,
        long limit
    )
    {
        var sourceStart = insertion.SourceStart;
        var sourceEnd = insertion.SourceEnd;
        if (
            sourceStart < 0
            || sourceEnd <= sourceStart
            || sourceEnd > limit
            || (sourceStart >= _publishedThrough && ByteAt(sourceStart, pendingBase, chunkStart, chunk) != (byte)'<')
        )
        {
            throw new InvalidOperationException("A recorded start-tag source range is outside the input.");
        }

        var position = sourceEnd - 1;
        if (position < _publishedThrough)
            throw new InvalidOperationException("Start-tag rewrite ranges are not ordered.");
        if (ByteAt(position, pendingBase, chunkStart, chunk) != (byte)'>')
            throw new InvalidOperationException("A recorded start-tag source range does not end at a tag close.");
        if (
            insertion.SelfClosing
            && position > sourceStart
            && position > _publishedThrough
            && ByteAt(position - 1, pendingBase, chunkStart, chunk) == (byte)'/'
        )
        {
            position--;
        }

        // Mirrors Utf8RewriteCollector.SegmentEnumerator: _publishedThrough plays the cursor.
        var needsSeparator =
            position == _publishedThrough
            || position == sourceStart
            || !Utf8RewriteCollector.IsHtmlSpace(ByteAt(position - 1, pendingBase, chunkStart, chunk));
        PublishRange(_publishedThrough, position, pendingBase, chunkStart, chunk);
        _publishedThrough = position;
        if (needsSeparator)
        {
            Write(" "u8);
        }
        Write(_payload.WrittenSpan.Slice(insertion.PayloadStart, insertion.PayloadLength));
    }

    private byte ByteAt(long offset, long pendingBase, long chunkStart, ReadOnlySpan<byte> chunk) =>
        offset < chunkStart ? _pending[(int)(offset - pendingBase)] : chunk[(int)(offset - chunkStart)];

    private void PublishRange(long from, long to, long pendingBase, long chunkStart, ReadOnlySpan<byte> chunk)
    {
        if (to <= from)
            return;

        if (from < chunkStart)
        {
            var pendingEnd = Math.Min(to, chunkStart);
            Write(_pending.AsSpan((int)(from - pendingBase), (int)(pendingEnd - from)));
            from = pendingEnd;
        }
        if (to > from)
        {
            Write(chunk[(int)(from - chunkStart)..(int)(to - chunkStart)]);
        }
    }

    private void Write(ReadOnlySpan<byte> value)
    {
        if (_sink is not null)
        {
            // Borrowed segments reach the sink by reference; no byte is copied.
            _sink(value);
            return;
        }

        // Bounded slices keep pipe-style writers on pooled segments instead of one huge rent.
        var output = _output!;
        while (!value.IsEmpty)
        {
            var slice = Math.Min(value.Length, 32 * 1024);
            value[..slice].CopyTo(output.GetSpan(slice));
            output.Advance(slice);
            value = value[slice..];
        }
    }

    private void EnsurePendingCapacity(int required)
    {
        if (required <= _pending.Length)
            return;

        var grown = ArrayPool<byte>.Shared.Rent(required);
        Array.Copy(_pending, grown, _pendingLength);
        ArrayPool<byte>.Shared.Return(_pending);
        _pending = grown;
    }

    private readonly record struct Insertion(
        long SourceStart,
        long SourceEnd,
        bool SelfClosing,
        int PayloadStart,
        int PayloadLength
    );
}
