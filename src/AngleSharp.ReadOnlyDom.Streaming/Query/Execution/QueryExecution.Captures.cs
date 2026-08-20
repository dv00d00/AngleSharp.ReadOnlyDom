using System.Numerics;
using System.Runtime.CompilerServices;

namespace AngleSharp.ReadOnlyDom.Streaming.Query.Execution;

internal partial class QueryExecution<TState, TResourceLimits>
    where TResourceLimits : struct, IResourceLimitPolicy
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DisposeCompletedCaptures()
    {
        foreach (var captures in _completedCaptures)
        {
            if (captures is null)
                continue;
            foreach (var capture in captures)
                capture.Dispose();
        }
        if (_reusableCaptures is not null)
        {
            foreach (var capture in _reusableCaptures)
                capture.Dispose();
            _reusableCaptures.Clear();
        }
        Array.Clear(_completedCaptures);
    }

    private void StartCompletedCaptures(ulong matches)
    {
        var completed = matches & _plan.CompletedHandlerMask;
        while (completed != 0)
        {
            var index = BitOperations.TrailingZeroCount(completed);
            completed &= completed - 1;
            var node = _plan.Nodes[index];
            var capture = _reusableCaptures!.Count == 0 ? new CapturedElementBuffer() : _reusableCaptures.Pop();
            capture.Reset(node.CompletedTextMode, node.CapturedAttributeIndexes.Length);
            for (var attribute = 0; attribute < node.CapturedAttributeIndexes.Length; attribute++)
            {
                var attributeIndex = node.CapturedAttributeIndexes[attribute];
                if (_attributeLengths[attributeIndex] >= 0)
                {
                    var value = GetAttributeValue(attributeIndex);
                    capture.SetAttribute(attribute, value);
                    if (TResourceLimits.Enabled)
                    {
                        _queryCaptureBytes += value.Length;
                    }
                }
            }
            capture.BeginText();
            var captures = _completedCaptures[index] ??= [];
            captures.Add(capture);
            if (node.CompletedTextMode != CompletedTextMode.None)
                _activeCompletedTextCaptures++;
            if (node.CompletedTextMode == CompletedTextMode.Normalized)
                _activeNormalizedTextCaptures++;
        }
    }

    /// <summary>
    /// Separates words in every open normalized capture. Callers have already established that this
    /// tag is a boundary and that at least one normalized capture is open, so this walks only the
    /// normalized nodes and never the raw ones.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void MarkTextBoundary()
    {
        var completed = _normalizedTextMask;
        while (completed != 0)
        {
            var index = BitOperations.TrailingZeroCount(completed);
            completed &= completed - 1;
            var captures = _completedCaptures[index];
            if (captures is null)
                continue;
            foreach (var capture in captures)
                capture.MarkBoundary();
        }
    }

    private void AppendCompletedText(ReadOnlySpan<byte> utf8)
    {
        var completed = _plan.CompletedHandlerMask;
        while (completed != 0)
        {
            var index = BitOperations.TrailingZeroCount(completed);
            completed &= completed - 1;
            var captures = _completedCaptures[index];
            if (captures is null)
                continue;
            foreach (var capture in captures)
            {
                var previousLength = capture.BufferedByteCount;
                capture.Append(utf8);
                if (TResourceLimits.Enabled)
                {
                    _queryCaptureBytes += capture.BufferedByteCount - previousLength;
                }
            }
        }
    }

    private void CompleteCapture(int index)
    {
        var node = _plan.Nodes[index];
        if (node.Completed is null)
            return;
        var captures = _completedCaptures[index];
        if (captures is null || captures.Count == 0)
            throw new InvalidOperationException("The completed-element capture stack is unbalanced.");
        var captureIndex = captures.Count - 1;
        var capture = captures[captureIndex];
        captures.RemoveAt(captureIndex);
        if (node.CompletedTextMode != CompletedTextMode.None)
            _activeCompletedTextCaptures--;
        if (node.CompletedTextMode == CompletedTextMode.Normalized)
            _activeNormalizedTextCaptures--;
        if (TResourceLimits.Enabled)
        {
            _queryCaptureBytes -= capture.BufferedByteCount;
        }
        try
        {
            var element = new CompletedElement(
                capture,
                _plan.AttributeNames,
                _plan.AttributeNamesUtf8,
                node.CapturedAttributeIndexes
            );
            node.Completed.Invoke(ref _state, in element);
        }
        finally
        {
            _reusableCaptures!.Push(capture);
        }
    }

    private long GetCompletedAttributeBytes(ulong matches)
    {
        var total = 0L;
        var completed = matches & _plan.CompletedHandlerMask;
        while (completed != 0)
        {
            var index = BitOperations.TrailingZeroCount(completed);
            completed &= completed - 1;
            foreach (var attributeIndex in _plan.Nodes[index].CapturedAttributeIndexes)
            {
                var length = _attributeLengths[attributeIndex];
                if (length > 0)
                    total = SaturatingAdd(total, length);
            }
        }
        return total;
    }

    private long GetCompletedTextUpperBound(int textLength)
    {
        if (textLength == 0)
            return 0;

        var total = 0L;
        var completed = _plan.CompletedHandlerMask;
        while (completed != 0)
        {
            var index = BitOperations.TrailingZeroCount(completed);
            completed &= completed - 1;
            if (_plan.Nodes[index].CompletedTextMode == CompletedTextMode.None)
                continue;
            var captures = _completedCaptures[index];
            if (captures is null)
                continue;
            foreach (var capture in captures)
            {
                total = SaturatingAdd(total, textLength);
                if (capture.HasPendingNormalizedSpace)
                    total = SaturatingAdd(total, 1);
            }
        }
        return total;
    }

    private void EnsureQueryCaptureCapacity(long additional)
    {
        var observed =
            _queryCaptureBytes > long.MaxValue - additional ? long.MaxValue : _queryCaptureBytes + additional;
        if (observed > _maximumQueryCaptureBytes)
            throw new HtmlStreamingLimitExceededException(
                HtmlStreamingLimit.QueryCaptureBytes,
                _maximumQueryCaptureBytes,
                observed
            );
    }

    private static long SaturatingAdd(long value, long additional) =>
        value > long.MaxValue - additional ? long.MaxValue : value + additional;
}
