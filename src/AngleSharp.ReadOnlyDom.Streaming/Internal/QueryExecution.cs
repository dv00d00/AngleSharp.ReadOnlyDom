using System.Buffers;
using System.Numerics;

namespace AngleSharp.ReadOnlyDom.Streaming;

internal sealed class QueryExecution<TState> : IUtf8HtmlTokenSink, IUtf8HtmlStartTagSourceRangeSink, IDisposable
{
    private readonly QueryPlan<TState> _plan;
    private readonly int[] _activeCounts;
    private QueryFrame[] _frames;
    private byte[] _attributeValues;
    private readonly int[] _attributeStarts;
    private readonly int[] _attributeLengths;
    private readonly List<CapturedElementBuffer>?[] _completedCaptures;
    private readonly Stack<CapturedElementBuffer>? _reusableCaptures;
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
    private readonly RewriteHandler<TState>? _rewriteHandler;
    private readonly Utf8RewriteCollector? _rewriteCollector;
    private long _startTagSourceStart = -1;
    private long _startTagSourceEnd = -1;
    private long _queryCaptureBytes;
    private int _activeTextNodes;
    private int _activeCompletedTextCaptures;

    internal QueryExecution(
        QueryPlan<TState> plan,
        TState state,
        HtmlStreamingLimits limits,
        RewriteHandler<TState>? rewriteHandler = null,
        Utf8RewriteCollector? rewriteCollector = null
    )
    {
        ArgumentNullException.ThrowIfNull(limits);
        _plan = plan;
        _state = state;
        _maximumNestingDepth = limits.MaximumNestingDepth;
        _maximumQueryCaptureBytes = limits.MaximumQueryCaptureBytes;
        _rewriteHandler = rewriteHandler;
        _rewriteCollector = rewriteCollector;
        _activeCounts = ArrayPool<int>.Shared.Rent(Math.Max(plan.Nodes.Length, 1));
        _activeCounts.AsSpan(0, plan.Nodes.Length).Clear();
        _frames = ArrayPool<QueryFrame>.Shared.Rent(64);
        _attributeValues = ArrayPool<byte>.Shared.Rent(256);
        _attributeStarts = ArrayPool<int>.Shared.Rent(Math.Max(plan.AttributeNames.Length, 1));
        _attributeLengths = ArrayPool<int>.Shared.Rent(Math.Max(plan.AttributeNames.Length, 1));
        _attributeLengths.AsSpan(0, plan.AttributeNames.Length).Fill(-1);
        _completedCaptures = plan.CompletedHandlerMask == 0 ? [] : new List<CapturedElementBuffer>?[plan.Nodes.Length];
        _reusableCaptures = plan.CompletedHandlerMask == 0 ? null : new Stack<CapturedElementBuffer>();
    }

    public TState State => _state;

    public Utf8HtmlTokenCapture Capture =>
        _activeTextNodes != 0 || _activeCompletedTextCaptures != 0
            ? Utf8HtmlTokenCapture.Text
            : Utf8HtmlTokenCapture.None;

    public bool WantsStartTagSourceRanges => _rewriteHandler is not null;

    public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name)
    {
        var hash = name.SemanticHash;
        _pendingTagHash = hash;
        _pendingTagLength = name.Verbatim.Length;
        _pendingCandidateBits = 0;
        _pendingAttributeBits = 0;
        var candidates = FindTagCandidates(hash, name.Verbatim.Length);
        while (candidates != 0)
        {
            var index = BitOperations.TrailingZeroCount(candidates);
            candidates &= candidates - 1;
            var node = _plan.Nodes[index];
            if (!name.SemanticEquals(node.TagNameUtf8) || !ParentMatches(node))
                continue;
            _pendingCandidateBits |= 1UL << node.Index;
            _pendingAttributeBits |= node.RequestedAttributeMask;
        }
        ResetAttributes();
        return _pendingAttributeBits == 0
            ? Utf8HtmlStartTagCapture.None
            : Utf8HtmlStartTagCapture.Attributes;
    }

    public bool WantsAttribute(Utf8HtmlName name)
    {
        var attributes = _pendingAttributeBits;
        while (attributes != 0)
        {
            var index = BitOperations.TrailingZeroCount(attributes);
            attributes &= attributes - 1;
            if (name.SemanticEquals(_plan.AttributeNamesUtf8[index]))
                return true;
        }
        return false;
    }

    public void Attribute(Utf8HtmlName name, ReadOnlySpan<byte> value)
    {
        var attributes = _pendingAttributeBits;
        while (attributes != 0)
        {
            var index = BitOperations.TrailingZeroCount(attributes);
            attributes &= attributes - 1;
            if (!name.SemanticEquals(_plan.AttributeNamesUtf8[index]))
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

    public void StartTagSourceRange(long sourceStart, long sourceEnd)
    {
        _startTagSourceStart = sourceStart;
        _startTagSourceEnd = sourceEnd;
    }

    public void StartTagEnd(bool selfClosing)
    {
        StartTagEndCore(selfClosing, _startTagSourceStart, _startTagSourceEnd);
        _startTagSourceStart = -1;
        _startTagSourceEnd = -1;
    }

    private void StartTagEndCore(bool selfClosing, long sourceStart, long sourceEnd)
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
            _plan.AttributeNamesUtf8,
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
        if (_rewriteHandler is not null && (matches & _plan.TerminalNodeMask) != 0)
        {
            if (sourceStart < 0 || sourceEnd <= sourceStart)
                throw new InvalidOperationException("The tokenizer did not provide a valid start-tag source range.");
            var editor = new StartTagEditor(_rewriteCollector!, sourceStart, sourceEnd, selfClosing);
            _rewriteHandler.Invoke(ref _state, in element, ref editor);
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
        if (_plan.TextHandlerMask == 0 && _plan.CompletedHandlerMask == 0)
            return;
        EnsureQueryCaptureCapacity(GetCompletedTextUpperBound(utf8.Length));
        var handlers = _plan.TextHandlerMask;
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

    public void EndTag(Utf8HtmlName name)
    {
        var hash = name.SemanticHash;
        for (var index = _frameCount - 1; index >= 0; index--)
        {
            if (_frames[index].TagHash != hash || _frames[index].TagLength != name.Verbatim.Length)
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

    private bool ParentMatches(QueryPlanNode<TState> node)
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
            if (node.CompletedTextMode != CompletedTextMode.None)
                _activeCompletedTextCaptures++;
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
                _queryCaptureBytes += capture.BufferedByteCount - previousLength;
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
        _queryCaptureBytes -= capture.BufferedByteCount;
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

    private void IncrementActive(ulong matches)
    {
        while (matches != 0)
        {
            var index = BitOperations.TrailingZeroCount(matches);
            matches &= matches - 1;
            if (_activeCounts[index] == 0 && (_plan.TextHandlerMask & (1UL << index)) != 0)
                _activeTextNodes++;
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
            if (_activeCounts[index] == 0 && (_plan.TextHandlerMask & (1UL << index)) != 0)
                _activeTextNodes--;
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
        var captureCount = 0L;
        var completed = _plan.CompletedHandlerMask;
        while (completed != 0)
        {
            var index = BitOperations.TrailingZeroCount(completed);
            completed &= completed - 1;
            captureCount = SaturatingAdd(captureCount, _completedCaptures[index]?.Count ?? 0);
        }
        return captureCount == 0 || textLength == 0 ? 0
            : captureCount > long.MaxValue / textLength ? long.MaxValue
            : captureCount * textLength;
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
        (length == 2 && (hash == HtmlVoidElements.BrHash || hash == HtmlVoidElements.HrHash))
        || (
            length == 3
            && (
                hash == HtmlVoidElements.ImgHash
                || hash == HtmlVoidElements.WbrHash
                || hash == HtmlVoidElements.ColHash
            )
        )
        || (
            length == 4
            && (
                hash == HtmlVoidElements.AreaHash
                || hash == HtmlVoidElements.BaseHash
                || hash == HtmlVoidElements.LinkHash
                || hash == HtmlVoidElements.MetaHash
            )
        )
        || (
            length == 5
            && (
                hash == HtmlVoidElements.EmbedHash
                || hash == HtmlVoidElements.InputHash
                || hash == HtmlVoidElements.ParamHash
                || hash == HtmlVoidElements.TrackHash
            )
        )
        || (length == 6 && hash == HtmlVoidElements.SourceHash);
}
