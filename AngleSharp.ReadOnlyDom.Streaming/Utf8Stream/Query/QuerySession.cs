using System.Buffers;
using System.Numerics;

namespace AngleSharp.ReadOnlyDom.Streaming.Utf8Stream.Query;

public sealed class QuerySession<TState> : IOptimizedUtf8HtmlTokenSink, IDisposable
{
    private readonly QueryPlan<TState> _plan;
    private readonly int[] _activeCounts;
    private QueryFrame[] _frames;
    private byte[] _attributeValues;
    private readonly int[] _attributeStarts;
    private readonly int[] _attributeLengths;
    private readonly List<ElementCapture>?[] _completedCaptures;
    private readonly Stack<ElementCapture>? _reusableCaptures;
    private TState _state;
    private int _frameCount;
    private int _attributeValueLength;
    private ulong _pendingTagHash;
    private int _pendingTagLength;
    private ulong _pendingCandidateBits;
    private ulong _pendingAttributeBits;
    private ulong _seenAttributeBits;
    private bool _disposed;
    private readonly int _maximumNestingDepth;
    private readonly long _maximumQueryCaptureBytes;
    private long _queryCaptureBytes;

    internal QuerySession(QueryPlan<TState> plan, TState state, HtmlStreamingLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _plan = plan;
        _state = state;
        _maximumNestingDepth = limits.MaximumNestingDepth;
        _maximumQueryCaptureBytes = limits.MaximumQueryCaptureBytes;
        _activeCounts = ArrayPool<int>.Shared.Rent(Math.Max(plan.Nodes.Length, 1));
        _activeCounts.AsSpan(0, plan.Nodes.Length).Clear();
        _frames = ArrayPool<QueryFrame>.Shared.Rent(64);
        _attributeValues = ArrayPool<byte>.Shared.Rent(256);
        _attributeStarts = ArrayPool<int>.Shared.Rent(Math.Max(plan.AttributeNames.Length, 1));
        _attributeLengths = ArrayPool<int>.Shared.Rent(Math.Max(plan.AttributeNames.Length, 1));
        _attributeLengths.AsSpan(0, plan.AttributeNames.Length).Fill(-1);
        _completedCaptures = plan.CompletedHandlerBits == 0 ? [] : new List<ElementCapture>?[plan.Nodes.Length];
        _reusableCaptures = plan.CompletedHandlerBits == 0 ? null : new Stack<ElementCapture>();
    }

    public TState State => _state;

    public void StartTag(ReadOnlySpan<byte> name) => StartTag(name, QueryCompiler.Hash(name));

    void IOptimizedUtf8HtmlTokenSink.StartTag(ReadOnlySpan<byte> name, ulong hash) => StartTag(name, hash);

    private void StartTag(ReadOnlySpan<byte> name, ulong hash)
    {
        _pendingTagHash = hash;
        _pendingTagLength = name.Length;
        _pendingCandidateBits = 0;
        _pendingAttributeBits = 0;
        var candidates = FindTagCandidates(hash, name.Length);
        while (candidates != 0)
        {
            var index = BitOperations.TrailingZeroCount(candidates);
            candidates &= candidates - 1;
            var node = _plan.Nodes[index];
            if (
                !name.SequenceEqual(node.TagName)
                || !ParentMatches(node)
            )
                continue;
            _pendingCandidateBits |= 1UL << node.Index;
            _pendingAttributeBits |= node.RequiredAttributeBits;
        }
        ResetAttributes();
    }

    bool IOptimizedUtf8HtmlTokenSink.WantsAttribute(ReadOnlySpan<byte> name)
    {
        var attributes = _pendingAttributeBits;
        while (attributes != 0)
        {
            var index = BitOperations.TrailingZeroCount(attributes);
            attributes &= attributes - 1;
            if (name.SequenceEqual(_plan.AttributeNameUtf8[index]))
                return true;
        }
        return false;
    }

    public void Attribute(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        var attributes = _pendingAttributeBits;
        while (attributes != 0)
        {
            var index = BitOperations.TrailingZeroCount(attributes);
            attributes &= attributes - 1;
            if (!name.SequenceEqual(_plan.AttributeNameUtf8[index]))
                continue;
            if (_attributeLengths[index] >= 0)
                return;
            EnsureQueryCaptureCapacity(value.Length);
            EnsureAttributeCapacity(value.Length);
            _attributeStarts[index] = _attributeValueLength;
            _attributeLengths[index] = value.Length;
            _seenAttributeBits |= 1UL << index;
            value.CopyTo(_attributeValues.AsSpan(_attributeValueLength));
            _attributeValueLength += value.Length;
            _queryCaptureBytes += value.Length;
            return;
        }
    }

    public void StartTagEnd(bool selfClosing)
    {
        var matches = 0UL;
        var candidates = _pendingCandidateBits;
        while (candidates != 0)
        {
            var index = BitOperations.TrailingZeroCount(candidates);
            candidates &= candidates - 1;
            var node = _plan.Nodes[index];
            if (!PredicatesMatch(node.Predicates))
                continue;
            matches |= 1UL << node.Index;
        }

        var closesImmediately = selfClosing || IsVoidTag(_pendingTagHash, _pendingTagLength);
        if (!closesImmediately && _frameCount >= _maximumNestingDepth)
            throw new HtmlStreamingLimitExceededException(
                HtmlStreamingLimit.NestingDepth,
                _maximumNestingDepth,
                (long)_frameCount + 1
            );
        EnsureQueryCaptureCapacity(GetCompletedAttributeBytes(matches));

        var element = new Element(
            _plan.AttributeNames,
            _plan.AttributeNameUtf8,
            _attributeValues,
            _attributeStarts,
            _attributeLengths
        );
        var starts = matches;
        while (starts != 0)
        {
            var index = BitOperations.TrailingZeroCount(starts);
            starts &= starts - 1;
            _plan.Nodes[index].Start?.Invoke(ref _state, in element);
        }
        StartCompletedCaptures(matches);

        if (closesImmediately)
        {
            CloseMatches(matches);
            return;
        }

        EnsureFrameCapacity();
        _frames[_frameCount++] = new QueryFrame(_pendingTagHash, _pendingTagLength, matches);
        IncrementActive(matches);
    }

    public void Text(ReadOnlySpan<byte> utf8)
    {
        EnsureQueryCaptureCapacity(GetCompletedTextUpperBound(utf8.Length));
        var handlers = _plan.TextHandlerBits;
        while (handlers != 0)
        {
            var nodeIndex = BitOperations.TrailingZeroCount(handlers);
            handlers &= handlers - 1;
            var count = _activeCounts[nodeIndex];
            for (var match = 0; match < count; match++)
                _plan.Nodes[nodeIndex].Text!.Invoke(ref _state, utf8);
        }
        AppendCompletedText(utf8);
    }

    public void EndTag(ReadOnlySpan<byte> name) => EndTag(name, QueryCompiler.Hash(name));

    void IOptimizedUtf8HtmlTokenSink.EndTag(ReadOnlySpan<byte> name, ulong hash) => EndTag(name, hash);

    private void EndTag(ReadOnlySpan<byte> name, ulong hash)
    {
        for (var index = _frameCount - 1; index >= 0; index--)
        {
            if (_frames[index].TagHash != hash || _frames[index].TagLength != name.Length)
                continue;
            for (var popped = _frameCount - 1; popped >= index; popped--)
                CloseFrame(_frames[popped]);
            _frameCount = index;
            return;
        }
    }

    private ulong FindTagCandidates(ulong hash, int length)
    {
        var entries = _plan.TagDispatch;
        var low = 0;
        var high = entries.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            var entry = entries[middle];
            var comparison = entry.Hash.CompareTo(hash);
            if (comparison == 0)
                comparison = entry.Length.CompareTo(length);
            if (comparison < 0)
                low = middle + 1;
            else if (comparison > 0)
                high = middle - 1;
            else
                return entry.CandidateBits;
        }
        return 0;
    }

    public void EndOfFile()
    {
        for (var index = _frameCount - 1; index >= 0; index--)
            CloseFrame(_frames[index]);
        _frameCount = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ArrayPool<int>.Shared.Return(_activeCounts, clearArray: true);
        ArrayPool<QueryFrame>.Shared.Return(_frames, clearArray: true);
        ArrayPool<byte>.Shared.Return(_attributeValues);
        ArrayPool<int>.Shared.Return(_attributeStarts, clearArray: true);
        ArrayPool<int>.Shared.Return(_attributeLengths, clearArray: true);
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
        _frames = [];
        _attributeValues = [];
    }

    private bool ParentMatches(CompiledQueryNode<TState> node)
    {
        if (node.ParentIndex < 0)
            return true;
        return node.Relation switch
        {
            QueryRelation.Descendant => _activeCounts[node.ParentIndex] != 0,
            QueryRelation.Child => _frameCount != 0
                                   && (_frames[_frameCount - 1].Matches & (1UL << node.ParentIndex)) != 0,
            _ => false,
        };
    }

    private bool PredicatesMatch(ReadOnlySpan<CompiledAttributePredicate> predicates)
    {
        foreach (var predicate in predicates)
        {
            var length = _attributeLengths[predicate.AttributeIndex];
            if (length < 0)
                return false;
            var value = _attributeValues.AsSpan(_attributeStarts[predicate.AttributeIndex], length);
            if (predicate.Kind == AttributePredicateKind.Equals && !value.SequenceEqual(predicate.Value))
                return false;
            if (predicate.Kind == AttributePredicateKind.ContainsToken && !ContainsToken(value, predicate.Value!))
                return false;
        }
        return true;
    }

    private void CloseFrame(QueryFrame frame)
    {
        CloseMatches(frame.Matches);
        DecrementActive(frame.Matches);
    }

    private void CloseMatches(ulong matches)
    {
        while (matches != 0)
        {
            var index = 63 - BitOperations.LeadingZeroCount(matches);
            matches &= ~(1UL << index);
            CompleteCapture(index);
            _plan.Nodes[index].End?.Invoke(ref _state);
        }
    }

    private void StartCompletedCaptures(ulong matches)
    {
        var completed = matches & _plan.CompletedHandlerBits;
        while (completed != 0)
        {
            var index = BitOperations.TrailingZeroCount(completed);
            completed &= completed - 1;
            var node = _plan.Nodes[index];
            var capture = _reusableCaptures!.Count == 0 ? new ElementCapture() : _reusableCaptures.Pop();
            capture.Reset(node.CompletedTextMode, node.CompletedAttributeIndexes.Length);
            for (var attribute = 0; attribute < node.CompletedAttributeIndexes.Length; attribute++)
            {
                var attributeIndex = node.CompletedAttributeIndexes[attribute];
                var length = _attributeLengths[attributeIndex];
                if (length >= 0)
                {
                    capture.SetAttribute(attribute, _attributeValues.AsSpan(_attributeStarts[attributeIndex], length));
                    _queryCaptureBytes += length;
                }
            }
            capture.BeginText();
            var captures = _completedCaptures[index] ??= [];
            captures.Add(capture);
        }
    }

    private void AppendCompletedText(ReadOnlySpan<byte> utf8)
    {
        var completed = _plan.CompletedHandlerBits;
        while (completed != 0)
        {
            var index = BitOperations.TrailingZeroCount(completed);
            completed &= completed - 1;
            var captures = _completedCaptures[index];
            if (captures is null)
                continue;
            foreach (var capture in captures)
            {
                var previousLength = capture.Length;
                capture.Append(utf8);
                _queryCaptureBytes += capture.Length - previousLength;
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
        _queryCaptureBytes -= capture.Length;
        try
        {
            var element = new CompletedElement(
                capture,
                _plan.AttributeNames,
                _plan.AttributeNameUtf8,
                node.CompletedAttributeIndexes
            );
            node.Completed.Invoke(ref _state, in element);
        }
        finally
        {
            _reusableCaptures!.Push(capture);
        }
    }

    private void IncrementActive(ulong matches)
    {
        while (matches != 0)
        {
            var index = BitOperations.TrailingZeroCount(matches);
            matches &= matches - 1;
            _activeCounts[index]++;
        }
    }

    private void DecrementActive(ulong matches)
    {
        while (matches != 0)
        {
            var index = BitOperations.TrailingZeroCount(matches);
            matches &= matches - 1;
            _activeCounts[index]--;
        }
    }

    private void ResetAttributes()
    {
        _queryCaptureBytes -= _attributeValueLength;
        _attributeValueLength = 0;
        while (_seenAttributeBits != 0)
        {
            var index = BitOperations.TrailingZeroCount(_seenAttributeBits);
            _seenAttributeBits &= _seenAttributeBits - 1;
            _attributeLengths[index] = -1;
        }
    }

    private void EnsureAttributeCapacity(int additional)
    {
        if (_attributeValueLength + additional <= _attributeValues.Length)
            return;
        var replacement = ArrayPool<byte>.Shared.Rent(
            Math.Max(_attributeValues.Length * 2, _attributeValueLength + additional)
        );
        _attributeValues.AsSpan(0, _attributeValueLength).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(_attributeValues);
        _attributeValues = replacement;
    }

    private void EnsureFrameCapacity()
    {
        if (_frameCount < _frames.Length)
            return;
        var replacement = ArrayPool<QueryFrame>.Shared.Rent(_frames.Length * 2);
        _frames.AsSpan(0, _frameCount).CopyTo(replacement);
        ArrayPool<QueryFrame>.Shared.Return(_frames, clearArray: true);
        _frames = replacement;
    }

    private long GetCompletedAttributeBytes(ulong matches)
    {
        var total = 0L;
        var completed = matches & _plan.CompletedHandlerBits;
        while (completed != 0)
        {
            var index = BitOperations.TrailingZeroCount(completed);
            completed &= completed - 1;
            foreach (var attributeIndex in _plan.Nodes[index].CompletedAttributeIndexes)
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
        var captureCount = 0L;
        var completed = _plan.CompletedHandlerBits;
        while (completed != 0)
        {
            var index = BitOperations.TrailingZeroCount(completed);
            completed &= completed - 1;
            captureCount = SaturatingAdd(captureCount, _completedCaptures[index]?.Count ?? 0);
        }
        return captureCount == 0 || textLength == 0
            ? 0
            : captureCount > long.MaxValue / textLength
                ? long.MaxValue
                : captureCount * textLength;
    }

    private void EnsureQueryCaptureCapacity(long additional)
    {
        var observed =
            _queryCaptureBytes > long.MaxValue - additional
                ? long.MaxValue
                : _queryCaptureBytes + additional;
        if (observed > _maximumQueryCaptureBytes)
            throw new HtmlStreamingLimitExceededException(
                HtmlStreamingLimit.QueryCaptureBytes,
                _maximumQueryCaptureBytes,
                observed
            );
    }

    private static long SaturatingAdd(long value, long additional) =>
        value > long.MaxValue - additional ? long.MaxValue : value + additional;

    private static bool ContainsToken(ReadOnlySpan<byte> tokens, ReadOnlySpan<byte> wanted)
    {
        var index = 0;
        while (index < tokens.Length)
        {
            while (index < tokens.Length && IsHtmlSpace(tokens[index]))
                index++;
            var start = index;
            while (index < tokens.Length && !IsHtmlSpace(tokens[index]))
                index++;
            if (tokens[start..index].SequenceEqual(wanted))
                return true;
        }
        return false;
    }

    private static bool IsHtmlSpace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0C;

    private static bool IsVoidTag(ulong hash, int length) =>
        (length == 2 && (hash == VoidElementHashes.BrHash || hash == VoidElementHashes.HrHash))
        || (
            length == 3
            && (
                hash == VoidElementHashes.ImgHash
                || hash == VoidElementHashes.WbrHash
                || hash == VoidElementHashes.ColHash
            )
        )
        || (
            length == 4
            && (
                hash == VoidElementHashes.AreaHash
                || hash == VoidElementHashes.BaseHash
                || hash == VoidElementHashes.LinkHash
                || hash == VoidElementHashes.MetaHash
            )
        )
        || (
            length == 5
            && (
                hash == VoidElementHashes.EmbedHash
                || hash == VoidElementHashes.InputHash
                || hash == VoidElementHashes.ParamHash
                || hash == VoidElementHashes.TrackHash
            )
        )
        || (length == 6 && hash == VoidElementHashes.SourceHash);
}
