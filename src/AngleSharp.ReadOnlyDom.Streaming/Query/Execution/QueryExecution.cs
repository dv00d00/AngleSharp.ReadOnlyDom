using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

namespace AngleSharp.ReadOnlyDom.Streaming.Query.Execution;

internal interface IQueryExecution<out TState> : IUtf8HtmlTokenSink, IDisposable
{
    TState State { get; }
}

internal partial class QueryExecution<TState, TResourceLimits>
    : IUtf8HtmlStartTagSourceRangeSink,
        IUtf8HtmlRawTextSink,
        IUtf8HtmlStreamingCommentSink,
        IElementAttributeSource,
        IQueryExecution<TState>
    where TResourceLimits : struct, IResourceLimitPolicy
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
    private ulong _pendingTagIdentity;
    private int _pendingTagIdentityLength;
    private int _pendingTagNameLength;
    private byte[]? _pendingFallbackTagNameUtf8;
    private ulong _pendingCandidateBits;
    private ulong _pendingAttributeBits;
    private ulong _pendingAttributeFilter;
    private ulong _pendingAttributeNameLengths;
    private int _pendingAttributeIndex = -1;
    private ulong _seenAttributeBits;
    private ulong _rawAttributeBits;
    private Utf8TokenBuffer? _decodeScratch;
    private bool _disposed;
    private readonly int _maximumNestingDepth;
    private readonly long _maximumQueryCaptureBytes;
    private readonly RewriteHandler<TState>? _elementRewriteHandler;
    private readonly TextRewriteHandler<TState>? _textRewriteHandler;
    private readonly IHtmlRewriteCollector? _rewriteCollector;
    private readonly Utf8StreamingRewriteCollector? _streamingRewriteCollector;
    private long _startTagSourceStart = -1;
    private long _startTagSourceEnd = -1;
    private long _endTagSourceStart = -1;
    private long _endTagSourceEnd = -1;
    private long _observedUtf8End;
    private long _queryCaptureBytes;
    private int _activeTextNodes;
    private int _activeCompletedTextCaptures;
    private int _activeNormalizedTextCaptures;
    private readonly ulong _normalizedTextMask;

    /// <summary>Set in <see cref="QueryFrame.TagIdentityLength"/> when the frame's tag separates words.</summary>
    private const int TextBoundaryFrameFlag = Int32.MinValue;

    /// <summary>Masks <see cref="TextBoundaryFrameFlag"/> off a frame's stored identity length.</summary>
    private const int TagIdentityLengthMask = Int32.MaxValue;

    // A plan has at most 64 nodes, so one field can carry the semantic and rewrite text counts.
    private const int RewriteTextNodeIncrement = 1 << 16;
    private const int RewriteTextNodeMask = unchecked((int)0xFFFF0000);

    internal QueryExecution(
        QueryPlan<TState> plan,
        TState state,
        HtmlStreamingLimits limits,
        RewriteHandler<TState>? rewriteHandler = null,
        TextRewriteHandler<TState>? textRewriteHandler = null,
        IHtmlRewriteCollector? rewriteCollector = null
    )
    {
        ArgumentNullException.ThrowIfNull(limits);
        _plan = plan;
        _state = state;
        _maximumNestingDepth = limits.MaximumNestingDepth;
        _maximumQueryCaptureBytes = limits.MaximumQueryCaptureBytes;
        _elementRewriteHandler = rewriteHandler;
        _textRewriteHandler = textRewriteHandler;
        _rewriteCollector = rewriteCollector;
        _normalizedTextMask = plan.NormalizedTextHandlerMask;
        _streamingRewriteCollector = rewriteCollector as Utf8StreamingRewriteCollector;
        _activeCounts = ArrayPool<int>.Shared.Rent(Math.Max(plan.Nodes.Length, 1));
        _activeCounts.AsSpan(0, plan.Nodes.Length).Clear();
        _frames = ArrayPool<QueryFrame>.Shared.Rent(Math.Min(64, _maximumNestingDepth));
        _attributeValues = [];
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

    public bool WantsStartTagSourceRanges => _elementRewriteHandler is not null || _textRewriteHandler is not null;

    public bool IsRawTextEnabled => HasTextRewriteHandler;

    public bool WantsRawText =>
        (_activeTextNodes & RewriteTextNodeMask) != 0 && _rewriteCollector?.IsSuppressingContent != true;

    public bool WantsEndTagSourceRanges => HasTextRewriteHandler || _rewriteCollector?.NeedsEndTagSourceRanges == true;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Frames popped by EndTag/EndOfFile are defaulted in place, so after a completed parse the
        // rented array holds no live references and can go back to the pool without a whole-array
        // clear. Only an abandoned (mid-parse) execution still owns frames and needs the cold sweep.
        if (_frameCount != 0 || _pendingFallbackTagNameUtf8 is not null)
            ReleaseLiveFrames();
        // The int arrays go back dirty on purpose: the constructor re-initializes exactly the
        // plan-sized prefixes it reads (_activeCounts cleared, _attributeLengths filled with -1),
        // and _attributeStarts is only ever read where the matching length is non-negative.
        ArrayPool<int>.Shared.Return(_activeCounts);
        ArrayPool<QueryFrame>.Shared.Return(_frames);
        ArrayPool<int>.Shared.Return(_attributeStarts);
        ArrayPool<int>.Shared.Return(_attributeLengths);
        if (_attributeValues.Length != 0)
            ArrayPool<byte>.Shared.Return(_attributeValues);
        if (_completedCaptures.Length != 0)
            DisposeCompletedCaptures();
        _frames = [];
        _attributeValues = [];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReleaseLiveFrames()
    {
        ReleasePendingFallbackTagName();
        for (var index = 0; index < _frameCount; index++)
        {
            ReleaseFallbackTagName(_frames[index]);
            _frames[index] = default;
        }
        _frameCount = 0;
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
            if (_attributeLengths[predicate.AttributeIndex] < 0)
                return false;
            // Existence never reads the value, so it must not trigger the lazy decode.
            if (predicate.Kind == AttributePredicateKind.Exists)
                continue;
            var value = GetAttributeValue(predicate.AttributeIndex);
            if (predicate.Kind == AttributePredicateKind.Equals && !value.SequenceEqual(predicate.Value))
                return false;
            if (predicate.Kind == AttributePredicateKind.ContainsToken && !ContainsToken(value, predicate.Value!))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Returns the stored attribute value with character references decoded, decoding lazily on
    /// first read. Values arrive raw from the tokenizer (most are never read); the decoded form
    /// is memoized by appending it to the value buffer and repointing the attribute's slot, so
    /// predicates, start handlers, and completed captures all observe it exactly once.
    /// </summary>
    private ReadOnlySpan<byte> GetAttributeValue(int index)
    {
        var length = _attributeLengths[index];
        var bit = 1UL << index;
        if ((_rawAttributeBits & bit) == 0)
            return _attributeValues.AsSpan(_attributeStarts[index], length);
        _rawAttributeBits &= ~bit;
        var raw = _attributeValues.AsSpan(_attributeStarts[index], length);
        var ampersand = raw.IndexOf((byte)'&');
        if (ampersand < 0)
            return raw;
        var scratch = _decodeScratch ??= new Utf8TokenBuffer(128);
        scratch.ResetWrittenCount();
        Utf8AttributeValueDecoder.Decode(raw, ampersand, scratch);
        var decoded = scratch.WrittenSpan;
        if (TResourceLimits.Enabled)
        {
            EnsureQueryCaptureCapacity(decoded.Length);
            _queryCaptureBytes += decoded.Length;
        }
        EnsureAttributeCapacity(decoded.Length);
        _attributeStarts[index] = _attributeValueLength;
        _attributeLengths[index] = decoded.Length;
        decoded.CopyTo(_attributeValues.AsSpan(_attributeValueLength));
        _attributeValueLength += decoded.Length;
        return _attributeValues.AsSpan(_attributeStarts[index], decoded.Length);
    }

    private void CloseFrame(QueryFrame frame, long sourceStart, long sourceEnd, bool hasExplicitEndTag)
    {
        if (frame.TagIdentityLength < 0 && _activeNormalizedTextCaptures != 0)
            MarkTextBoundary();
        try
        {
            _rewriteCollector?.EndElement(frame.RewriteScopeId, sourceStart, sourceEnd, hasExplicitEndTag);
            CloseMatches(frame.Matches);
            DecrementActive(frame.Matches);
        }
        finally
        {
            ReleaseFallbackTagName(frame);
        }
    }

    private void ReleasePendingFallbackTagName()
    {
        if (_pendingFallbackTagNameUtf8 is null)
            return;
        ArrayPool<byte>.Shared.Return(_pendingFallbackTagNameUtf8);
        _pendingFallbackTagNameUtf8 = null;
    }

    private static void ReleaseFallbackTagName(QueryFrame frame)
    {
        if (frame.FallbackTagNameUtf8 is not null)
            ArrayPool<byte>.Shared.Return(frame.FallbackTagNameUtf8);
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

    private void IncrementActive(ulong matches)
    {
        while (matches != 0)
        {
            var index = BitOperations.TrailingZeroCount(matches);
            matches &= matches - 1;
            if (_activeCounts[index] == 0 && (_plan.TextHandlerMask & (1UL << index)) != 0)
                _activeTextNodes++;
            if (HasTextRewriteHandler && _activeCounts[index] == 0 && (_plan.TerminalNodeMask & (1UL << index)) != 0)
                _activeTextNodes += RewriteTextNodeIncrement;
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
            if (HasTextRewriteHandler && _activeCounts[index] == 0 && (_plan.TerminalNodeMask & (1UL << index)) != 0)
                _activeTextNodes -= RewriteTextNodeIncrement;
        }
    }

    private void ResetAttributes()
    {
        if (TResourceLimits.Enabled)
        {
            _queryCaptureBytes -= _attributeValueLength;
        }
        _attributeValueLength = 0;
        _rawAttributeBits = 0;
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
            Math.Max(Math.Max(256, _attributeValues.Length * 2), _attributeValueLength + additional)
        );
        _attributeValues.AsSpan(0, _attributeValueLength).CopyTo(replacement);
        if (_attributeValues.Length != 0)
            ArrayPool<byte>.Shared.Return(_attributeValues);
        _attributeValues = replacement;
    }

    private void EnsureFrameCapacity()
    {
        if (_frameCount < _frames.Length)
            return;
        var replacement = ArrayPool<QueryFrame>.Shared.Rent(_frames.Length * 2);
        _frames.AsSpan(0, _frameCount).CopyTo(replacement);
        _frames.AsSpan(0, _frameCount).Clear();
        ArrayPool<QueryFrame>.Shared.Return(_frames);
        _frames = replacement;
    }

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

    private static bool IsVoidTag(ulong identity, int identityLength, int nameLength) =>
        identityLength == 0
        && (
            (nameLength == 2 && (identity == HtmlVoidElements.Br || identity == HtmlVoidElements.Hr))
            || (
                nameLength == 3
                && (
                    identity == HtmlVoidElements.Img
                    || identity == HtmlVoidElements.Wbr
                    || identity == HtmlVoidElements.Col
                )
            )
            || (
                nameLength == 4
                && (
                    identity == HtmlVoidElements.Area
                    || identity == HtmlVoidElements.Base
                    || identity == HtmlVoidElements.Link
                    || identity == HtmlVoidElements.Meta
                )
            )
            || (
                nameLength == 5
                && (
                    identity == HtmlVoidElements.Embed
                    || identity == HtmlVoidElements.Input
                    || identity == HtmlVoidElements.Param
                    || identity == HtmlVoidElements.Track
                )
            )
            || (nameLength == 6 && identity == HtmlVoidElements.Source)
        );

    private bool HasTextRewriteHandler => _textRewriteHandler is not null;

    private RewriteHandler<TState>? ElementRewriteHandler => _elementRewriteHandler;

    private TextRewriteHandler<TState> TextRewriteHandler => _textRewriteHandler!;
}

internal sealed class QueryExecution<TState> : QueryExecution<TState, EnforcedResourceLimits>
{
    internal QueryExecution(
        QueryPlan<TState> plan,
        TState state,
        HtmlStreamingLimits limits,
        RewriteHandler<TState>? rewriteHandler = null,
        TextRewriteHandler<TState>? textRewriteHandler = null,
        IHtmlRewriteCollector? rewriteCollector = null
    )
        : base(plan, state, limits, rewriteHandler, textRewriteHandler, rewriteCollector) { }
}
