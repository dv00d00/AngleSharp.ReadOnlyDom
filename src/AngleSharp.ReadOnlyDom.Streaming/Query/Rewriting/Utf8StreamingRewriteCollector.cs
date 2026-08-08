using System.Buffers;

namespace AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;

/// <summary>
/// Applies element mutations while normalized input is still borrowed. Untouched bytes cross once;
/// removed descendants are discarded as they arrive instead of being retained until the end tag.
/// </summary>
internal sealed class Utf8StreamingRewriteCollector : HtmlRewriteCollectorBase, IDisposable
{
    private readonly IBufferWriter<byte>? _output;
    private readonly StreamingRewriteSegmentSink? _sink;
    private readonly int _maximumHoldbackBytes;
    private readonly List<RewriteEvent> _events = [];
    private byte[] _pending;
    private int _pendingLength;
    private long _publishedThrough;
    private long _observedEnd;
    private int _logicalSuppressionDepth;
    private int _outputSuppressionScope = -1;

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

    public override void CommitElement(int scopeId)
    {
        base.CommitElement(scopeId);
        if (scopeId < 0)
            return;
        var mutation = Mutation(scopeId);
        mutation.Ignored = _logicalSuppressionDepth != 0;
        if (mutation.Ignored)
            return;
        mutation.OpensSuppression =
            mutation.CanHaveContent
            && (
                mutation.Disposition is ElementDisposition.Remove or ElementDisposition.Replace
                || mutation.SuppressInnerContent
            );
        if (mutation.OpensSuppression)
            _logicalSuppressionDepth++;
        _events.Add(new RewriteEvent(scopeId, IsStart: true));
    }

    public override void EndElement(int scopeId, long sourceStart, long sourceEnd, bool hasExplicitEndTag)
    {
        if (scopeId < 0)
            return;
        var mutation = Mutation(scopeId);
        base.EndElement(scopeId, sourceStart, sourceEnd, hasExplicitEndTag);
        if (mutation.Ignored)
            return;
        if (mutation.OpensSuppression)
            _logicalSuppressionDepth--;
        if (
            mutation.OpensSuppression
            || (
                hasExplicitEndTag
                && (
                    mutation.Append.Count != 0
                    || mutation.After.Count != 0
                    || mutation.Disposition == ElementDisposition.Unwrap
                )
            )
        )
        {
            _events.Add(new RewriteEvent(scopeId, IsStart: false));
        }
    }

    internal void PublishWindow(long chunkStart, ReadOnlySpan<byte> chunk, long watermark)
    {
        if (chunkStart != _observedEnd)
            throw new InvalidOperationException("The observed input stream is not contiguous.");
        _observedEnd += chunk.Length;

        var pendingBase = chunkStart - _pendingLength;
        var limit = Math.Min(watermark, _observedEnd);
        var processedEvents = 0;
        foreach (var rewriteEvent in _events)
        {
            var mutation = Mutation(rewriteEvent.ScopeId);
            var eventEnd = rewriteEvent.IsStart ? mutation.SourceEnd : mutation.EndEnd;
            if (eventEnd > limit)
                break;
            if (rewriteEvent.IsStart)
                ApplyStart(rewriteEvent.ScopeId, mutation, pendingBase, chunkStart, chunk);
            else
                ApplyEnd(rewriteEvent.ScopeId, mutation, pendingBase, chunkStart, chunk);
            processedEvents++;
        }
        if (processedEvents != 0)
            _events.RemoveRange(0, processedEvents);

        if (_outputSuppressionScope >= 0)
            _publishedThrough = limit;
        else
        {
            PublishRange(_publishedThrough, limit, pendingBase, chunkStart, chunk);
            _publishedThrough = limit;
        }

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
            Array.Copy(_pending, (int)(Math.Max(limit, pendingBase) - pendingBase), _pending, 0, pendingCarry);
        if (chunkCarry > 0)
            chunk[chunkCarryStart..].CopyTo(_pending.AsSpan(pendingCarry));
        _pendingLength = carry;
    }

    internal void Finish() => PublishWindow(_observedEnd, [], _observedEnd);

    public void Dispose()
    {
        var pending = _pending;
        _pending = [];
        _pendingLength = 0;
        if (pending.Length > 0)
            ArrayPool<byte>.Shared.Return(pending);
    }

    private void ApplyStart(
        int scopeId,
        HtmlElementMutation mutation,
        long pendingBase,
        long chunkStart,
        ReadOnlySpan<byte> chunk
    )
    {
        if (_outputSuppressionScope >= 0 || mutation.SourceStart < _publishedThrough)
            throw new InvalidOperationException("Element rewrite events are not ordered.");
        PublishRange(_publishedThrough, mutation.SourceStart, pendingBase, chunkStart, chunk);
        _publishedThrough = mutation.SourceStart;
        WriteForward(mutation.Before);

        if (mutation.Disposition is ElementDisposition.Remove or ElementDisposition.Replace)
        {
            if (mutation.Disposition == ElementDisposition.Replace)
                Write(mutation.Replacement!);
            _publishedThrough = mutation.SourceEnd;
            if (mutation.CanHaveContent)
                _outputSuppressionScope = scopeId;
            else
                WriteReverse(mutation.After);
            return;
        }

        if (mutation.Disposition != ElementDisposition.Unwrap)
        {
            if (mutation.ChangesStartTag)
            {
                var sourceTag = CopyRange(mutation.SourceStart, mutation.SourceEnd, pendingBase, chunkStart, chunk);
                Write(StartTagMutationWriter.Rewrite(sourceTag, mutation));
            }
            else
            {
                PublishRange(mutation.SourceStart, mutation.SourceEnd, pendingBase, chunkStart, chunk);
            }
        }
        _publishedThrough = mutation.SourceEnd;
        WriteReverse(mutation.Prepend);
        if (mutation.InnerReplacement is not null)
            Write(mutation.InnerReplacement);
        if (mutation.SuppressInnerContent)
            _outputSuppressionScope = scopeId;
        if (!mutation.CanHaveContent)
            WriteReverse(mutation.After);
    }

    private void ApplyEnd(
        int scopeId,
        HtmlElementMutation mutation,
        long pendingBase,
        long chunkStart,
        ReadOnlySpan<byte> chunk
    )
    {
        if (mutation.OpensSuppression)
        {
            if (_outputSuppressionScope != scopeId)
                throw new InvalidOperationException("Element suppression scopes are not nested correctly.");
            _publishedThrough = mutation.Disposition is ElementDisposition.Remove or ElementDisposition.Replace
                ? mutation.EndEnd
                : mutation.EndStart;
            _outputSuppressionScope = -1;
            if (mutation.Disposition is ElementDisposition.Remove or ElementDisposition.Replace)
            {
                if (mutation.HasExplicitEndTag)
                    WriteReverse(mutation.After);
                return;
            }
        }
        else
        {
            PublishRange(_publishedThrough, mutation.EndStart, pendingBase, chunkStart, chunk);
            _publishedThrough = mutation.EndStart;
        }

        if (!mutation.HasExplicitEndTag)
            return;
        WriteForward(mutation.Append);
        if (mutation.Disposition != ElementDisposition.Unwrap)
            PublishRange(mutation.EndStart, mutation.EndEnd, pendingBase, chunkStart, chunk);
        _publishedThrough = mutation.EndEnd;
        WriteReverse(mutation.After);
    }

    private byte[] CopyRange(long from, long to, long pendingBase, long chunkStart, ReadOnlySpan<byte> chunk)
    {
        var length = checked((int)(to - from));
        var output = new byte[length];
        var written = 0;
        if (from < chunkStart)
        {
            var pendingEnd = Math.Min(to, chunkStart);
            var count = checked((int)(pendingEnd - from));
            _pending.AsSpan(checked((int)(from - pendingBase)), count).CopyTo(output);
            written += count;
            from = pendingEnd;
        }
        if (from < to)
            chunk.Slice(checked((int)(from - chunkStart)), checked((int)(to - from))).CopyTo(output.AsSpan(written));
        return output;
    }

    private void PublishRange(long from, long to, long pendingBase, long chunkStart, ReadOnlySpan<byte> chunk)
    {
        if (to <= from)
            return;
        if (from < pendingBase || to > chunkStart + chunk.Length)
            throw new InvalidOperationException(
                $"Rewrite range [{from}, {to}) is outside [{pendingBase}, {chunkStart + chunk.Length}); "
                    + $"published={_publishedThrough}, pending={_pendingLength}."
            );
        if (from < chunkStart)
        {
            var pendingEnd = Math.Min(to, chunkStart);
            Write(_pending.AsSpan(checked((int)(from - pendingBase)), checked((int)(pendingEnd - from))));
            from = pendingEnd;
        }
        if (to > from)
            Write(chunk[checked((int)(from - chunkStart))..checked((int)(to - chunkStart))]);
    }

    private void WriteForward(List<byte[]> values)
    {
        foreach (var value in values)
            Write(value);
    }

    private void WriteReverse(List<byte[]> values)
    {
        for (var index = values.Count - 1; index >= 0; index--)
            Write(values[index]);
    }

    private void Write(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            return;
        if (_sink is not null)
        {
            _sink(value);
            return;
        }
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

    private readonly record struct RewriteEvent(int ScopeId, bool IsStart);
}
