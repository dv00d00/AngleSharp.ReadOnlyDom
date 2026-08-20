using System.Buffers;
using System.Numerics;
using AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

namespace AngleSharp.ReadOnlyDom.Streaming.Query.Execution;

internal partial class QueryExecution<TState, TResourceLimits>
    where TResourceLimits : struct, IResourceLimitPolicy
{
    public void ObserveNormalizedUtf8End(long sourceStart, ReadOnlySpan<byte> utf8, long publishableOffset)
    {
        _observedUtf8End = sourceStart + utf8.Length;
        _streamingRewriteCollector?.PublishWindow(sourceStart, utf8, publishableOffset);
    }

    public void RawText(long sourceStart, ReadOnlySpan<byte> utf8, Utf8HtmlTextType textType, bool isLastInTextNode)
    {
        if (!WantsRawText)
            return;
        var chunk = new TextChunk(utf8, (HtmlTextType)textType, isLastInTextNode);
        var rewriter = new TextChunkRewriter(_rewriteCollector!, sourceStart, sourceStart + utf8.Length);
        TextRewriteHandler.Invoke(ref _state, in chunk, ref rewriter);
        rewriter.Commit();
    }

    public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name)
    {
        ReleasePendingFallbackTagName();
        var identityLength = 0;
        if (!name.TryGetCompactKey(out var identity))
        {
            identity = name.SemanticHash;
            identityLength = name.Verbatim.Length;
            _pendingFallbackTagNameUtf8 = ArrayPool<byte>.Shared.Rent(identityLength);
            name.Verbatim.CopyTo(_pendingFallbackTagNameUtf8);
        }
        _pendingTagIdentity = identity;
        _pendingTagIdentityLength = identityLength;
        _pendingTagNameLength = name.Verbatim.Length;
        _pendingCandidateBits = 0;
        _pendingAttributeBits = 0;
        _pendingAttributeFilter = 0;
        _pendingAttributeNameLengths = 0;
        _pendingAttributeIndex = -1;
        var candidates = FindTagCandidates(identity, identityLength);
        while (candidates != 0)
        {
            var index = BitOperations.TrailingZeroCount(candidates);
            candidates &= candidates - 1;
            var node = _plan.Nodes[index];
            if ((identityLength != 0 && !name.SemanticEquals(node.TagNameUtf8)) || !ParentMatches(node))
                continue;
            _pendingCandidateBits |= 1UL << node.Index;
            _pendingAttributeBits |= node.RequestedAttributeMask;
            _pendingAttributeFilter |= node.RequestedAttributeFilter;
            _pendingAttributeNameLengths |= node.RequestedAttributeNameLengths;
        }
        ResetAttributes();
        return _pendingAttributeBits == 0 ? Utf8HtmlStartTagCapture.None : Utf8HtmlStartTagCapture.Attributes;
    }

    /// <summary>
    /// Bloom of the semantic hashes of every attribute name any candidate node on the current
    /// tag requests. WantsAttribute is a pure function of the semantic name for the duration of
    /// the tag (it only consults _pendingAttributeBits, fixed at StartTag), so the tokenizer may
    /// reject filter-missed names without calling back.
    /// </summary>
    public ulong StartTagAttributeFilter => _pendingAttributeFilter;

    /// <summary>
    /// Byte lengths of the attribute names any candidate node on the current tag requests, as bits.
    /// Same purity contract as <see cref="StartTagAttributeFilter"/>, and cheaper for the tokenizer
    /// to consult: a length is known before the name has been hashed.
    /// </summary>
    public ulong StartTagAttributeNameLengths => _pendingAttributeNameLengths;

    public bool WantsAttribute(Utf8HtmlName name)
    {
        _pendingAttributeIndex = -1;
        var identity = 0UL;
        var hasCompactIdentity =
            (_pendingAttributeBits & _plan.CompactAttributeMask) != 0 && name.TryGetCompactKey(out identity);

        var attributes = _pendingAttributeBits;
        while (attributes != 0)
        {
            var index = BitOperations.TrailingZeroCount(attributes);
            attributes &= attributes - 1;
            var expected = _plan.AttributeIdentities[index];
            if (hasCompactIdentity)
            {
                if (expected.Length != 0 || expected.Value != identity)
                    continue;
            }
            else if (
                expected.Length == 0
                || expected.Length != name.Verbatim.Length
                || !name.SemanticEquals(_plan.AttributeNamesUtf8[index])
            )
            {
                continue;
            }
            _pendingAttributeIndex = index;
            return true;
        }
        return false;
    }

    public void Attribute(Utf8HtmlName name, ReadOnlySpan<byte> value, bool valueMayContainReferences)
    {
        var index = _pendingAttributeIndex;
        _pendingAttributeIndex = -1;
        if (index < 0 || _attributeLengths[index] >= 0)
            return;
        if (TResourceLimits.Enabled)
        {
            EnsureQueryCaptureCapacity(value.Length);
        }
        EnsureAttributeCapacity(value.Length);
        _attributeStarts[index] = _attributeValueLength;
        _attributeLengths[index] = value.Length;
        _seenAttributeBits |= 1UL << index;
        if (valueMayContainReferences)
            _rawAttributeBits |= 1UL << index;
        value.CopyTo(_attributeValues.AsSpan(_attributeValueLength));
        _attributeValueLength += value.Length;
        if (TResourceLimits.Enabled)
        {
            _queryCaptureBytes += value.Length;
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
        // Classify only inside an open normalized capture, then carry the result to the close in
        // the frame's sign bit. A frame opened before the outermost capture cannot close while that
        // capture is active: lexical recovery closes inner frames first.
        var isTextBoundary =
            _activeNormalizedTextCaptures != 0
            && HtmlTextBoundaryElements.IsBoundary(_pendingTagIdentity, _pendingTagIdentityLength);
        if (isTextBoundary)
            MarkTextBoundary();
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

        var closesImmediately = IsVoidTag(_pendingTagIdentity, _pendingTagIdentityLength, _pendingTagNameLength);
        if (TResourceLimits.Enabled && !closesImmediately && _frameCount >= _maximumNestingDepth)
            throw new HtmlStreamingLimitExceededException(
                HtmlStreamingLimit.NestingDepth,
                _maximumNestingDepth,
                (long)_frameCount + 1
            );
        if (TResourceLimits.Enabled)
        {
            EnsureQueryCaptureCapacity(GetCompletedAttributeBytes(matches));
        }

        var starts = matches;
        while (starts != 0)
        {
            var index = BitOperations.TrailingZeroCount(starts);
            starts &= starts - 1;
            var node = _plan.Nodes[index];
            if (node.Start is null)
                continue;
            var element = CreateElement(node.RequestedAttributeMask);
            node.Start.Invoke(ref _state, in element);
        }
        var rewriteScopeId = -1;
        var rewriteHandler = ElementRewriteHandler;
        if (rewriteHandler is not null && (matches & _plan.TerminalNodeMask) != 0)
        {
            if (sourceStart < 0 || sourceEnd <= sourceStart)
                throw new InvalidOperationException("The tokenizer did not provide a valid start-tag source range.");
            var element = CreateElement(GetRequestedAttributeMask(matches & _plan.TerminalNodeMask));
            var editor = new ElementRewriter(
                _rewriteCollector!,
                sourceStart,
                sourceEnd,
                !closesImmediately,
                selfClosing
            );
            rewriteHandler.Invoke(ref _state, in element, ref editor);
            editor.Commit();
            rewriteScopeId = editor.ScopeId;
        }
        StartCompletedCaptures(matches);

        if (closesImmediately)
        {
            try
            {
                _rewriteCollector?.EndElement(rewriteScopeId, sourceEnd, sourceEnd, hasExplicitEndTag: false);
                CloseMatches(matches);
            }
            finally
            {
                ReleasePendingFallbackTagName();
            }
            return;
        }

        EnsureFrameCapacity();
        _frames[_frameCount++] = new QueryFrame(
            _pendingTagIdentity,
            isTextBoundary ? _pendingTagIdentityLength | TextBoundaryFrameFlag : _pendingTagIdentityLength,
            _pendingFallbackTagNameUtf8,
            matches,
            rewriteScopeId
        );
        _pendingFallbackTagNameUtf8 = null;
        IncrementActive(matches);
    }

    private Element CreateElement(ulong allowedAttributeMask) =>
        new(_plan.AttributeNames, _plan.AttributeNamesUtf8, this, allowedAttributeMask);

    bool IElementAttributeSource.TryGetAttributeValue(int index, out ReadOnlySpan<byte> value)
    {
        if (_attributeLengths[index] < 0)
        {
            value = default;
            return false;
        }
        value = GetAttributeValue(index);
        return true;
    }

    private ulong GetRequestedAttributeMask(ulong nodes)
    {
        var attributes = 0UL;
        while (nodes != 0)
        {
            var index = BitOperations.TrailingZeroCount(nodes);
            nodes &= nodes - 1;
            attributes |= _plan.Nodes[index].RequestedAttributeMask;
        }
        return attributes;
    }

    public void Text(ReadOnlySpan<byte> utf8)
    {
        if (_plan.TextHandlerMask == 0 && _plan.CompletedHandlerMask == 0)
            return;
        if (TResourceLimits.Enabled)
        {
            EnsureQueryCaptureCapacity(GetCompletedTextUpperBound(utf8.Length));
        }
        var handlers = _plan.TextHandlerMask;
        while (handlers != 0)
        {
            var nodeIndex = BitOperations.TrailingZeroCount(handlers);
            handlers &= handlers - 1;
            if (_activeCounts[nodeIndex] == 0)
                continue;
            _plan.Nodes[nodeIndex].Text!.Invoke(ref _state, utf8);
        }
        AppendCompletedText(utf8);
    }

    bool IUtf8HtmlStreamingCommentSink.BeginComment() => false;

    void IUtf8HtmlStreamingCommentSink.CommentChunk(ReadOnlySpan<byte> utf8) { }

    void IUtf8HtmlStreamingCommentSink.EndComment() { }

    public void EndTagSourceRange(long sourceStart, long sourceEnd)
    {
        _endTagSourceStart = sourceStart;
        _endTagSourceEnd = sourceEnd;
    }

    public void EndTag(Utf8HtmlName name)
    {
        var identityLength = 0;
        if (!name.TryGetCompactKey(out var identity))
        {
            identity = name.SemanticHash;
            identityLength = name.Verbatim.Length;
        }
        for (var index = _frameCount - 1; index >= 0; index--)
        {
            if (
                _frames[index].TagIdentity != identity
                || (_frames[index].TagIdentityLength & TagIdentityLengthMask) != identityLength
            )
                continue;
            if (
                identityLength != 0
                && !name.SemanticEquals(_frames[index].FallbackTagNameUtf8.AsSpan(0, identityLength))
            )
                continue;
            for (var popped = _frameCount - 1; popped >= index; popped--)
            {
                var frame = _frames[popped];
                _frames[popped] = default;
                _frameCount = popped;
                var explicitEnd = popped == index;
                CloseFrame(frame, _endTagSourceStart, explicitEnd ? _endTagSourceEnd : _endTagSourceStart, explicitEnd);
            }
            _endTagSourceStart = -1;
            _endTagSourceEnd = -1;
            return;
        }
        _endTagSourceStart = -1;
        _endTagSourceEnd = -1;
    }

    private ulong FindTagCandidates(ulong identity, int identityLength)
    {
        var entries = _plan.TagDispatch;
        var low = 0;
        var high = entries.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            var entry = entries[middle];
            var comparison = entry.Identity.CompareTo(identity);
            if (comparison == 0)
                comparison = entry.IdentityLength.CompareTo(identityLength);
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
        {
            var frame = _frames[index];
            _frames[index] = default;
            _frameCount = index;
            CloseFrame(frame, _observedUtf8End, _observedUtf8End, hasExplicitEndTag: false);
        }
    }
}
