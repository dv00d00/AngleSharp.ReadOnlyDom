using System.Buffers;
using System.IO.Pipelines;
using System.Numerics;
using System.Text;

namespace AngleSharp.ReadOnlyDom.Streaming.Utf8Stream;

public enum QueryRelation : byte
{
    Root,
    Descendant,
    Child,
}

public delegate void StartHandler<TState>(ref TState state, in Element element);

public delegate void TextHandler<TState>(ref TState state, ReadOnlySpan<byte> utf8);

public delegate void EndHandler<TState>(ref TState state);

public delegate void CompletedElementHandler<TState>(ref TState state, in CompletedElement element);

public readonly ref struct CompletedElement
{
    private readonly ElementCapture _capture;
    private readonly string[] _attributeNames;
    private readonly byte[][] _attributeNamesUtf8;
    private readonly int[] _attributeIndexes;

    internal CompletedElement(
        ElementCapture capture,
        string[] attributeNames,
        byte[][] attributeNamesUtf8,
        int[] attributeIndexes
    )
    {
        _capture = capture;
        _attributeNames = attributeNames;
        _attributeNamesUtf8 = attributeNamesUtf8;
        _attributeIndexes = attributeIndexes;
    }

    /// <summary>Borrowed normalized or raw UTF-8 text, valid only during the callback.</summary>
    public ReadOnlySpan<byte> TextUtf8 => _capture.TextUtf8;

    /// <summary>Returns an owned UTF-16 string, decoding only when requested.</summary>
    public string GetText() => _capture.GetText();

    public bool TryGetAttributeUtf8(string name, out ReadOnlySpan<byte> value)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (var index = 0; index < _attributeIndexes.Length; index++)
        {
            if (_attributeNames[_attributeIndexes[index]].Equals(name, StringComparison.OrdinalIgnoreCase))
                return _capture.TryGetAttribute(index, out value);
        }
        value = default;
        return false;
    }

    /// <summary>The UTF-8 attribute name must use the normalized lowercase spelling compiled by the query.</summary>
    public bool TryGetAttributeUtf8(ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)
    {
        for (var index = 0; index < _attributeIndexes.Length; index++)
        {
            if (_attributeNamesUtf8[_attributeIndexes[index]].AsSpan().SequenceEqual(name))
                return _capture.TryGetAttribute(index, out value);
        }
        value = default;
        return false;
    }

    public string? GetAttribute(string name) =>
        TryGetAttributeUtf8(name, out var value) ? Encoding.UTF8.GetString(value) : null;

    public string GetAttributeOrEmpty(string name) => GetAttribute(name) ?? string.Empty;
}

internal enum CompletedTextMode : byte
{
    None,
    Raw,
    Normalized,
}

public readonly ref struct Element
{
    private readonly string[] _attributeNames;
    private readonly byte[][] _attributeNameUtf8;
    private readonly byte[] _values;
    private readonly int[] _starts;
    private readonly int[] _lengths;

    internal Element(string[] attributeNames, byte[][] attributeNameUtf8, byte[] values, int[] starts, int[] lengths)
    {
        _attributeNames = attributeNames;
        _attributeNameUtf8 = attributeNameUtf8;
        _values = values;
        _starts = starts;
        _lengths = lengths;
    }

    public bool HasAttribute(string name) => TryGetAttribute(name, out _);

    public bool TryGetAttribute(string name, out ReadOnlySpan<byte> value)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (var index = 0; index < _attributeNames.Length; index++)
        {
            if (_attributeNames[index].Equals(name, StringComparison.Ordinal))
                return TryGetAttribute(index, out value);
        }
        value = default;
        return false;
    }

    public bool TryGetAttribute(ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)
    {
        for (var index = 0; index < _attributeNameUtf8.Length; index++)
        {
            if (name.SequenceEqual(_attributeNameUtf8[index]))
                return TryGetAttribute(index, out value);
        }
        value = default;
        return false;
    }

    private bool TryGetAttribute(int index, out ReadOnlySpan<byte> value)
    {
        var length = _lengths[index];
        if (length < 0)
        {
            value = default;
            return false;
        }
        value = _values.AsSpan(_starts[index], length);
        return true;
    }
}

public sealed class Selector
{
    private readonly List<AttributePredicate> _attributes = [];

    private Selector(string tagName) => TagName = NormalizeName(tagName, nameof(tagName));

    public string TagName { get; }

    internal IReadOnlyList<AttributePredicate> Attributes => _attributes;

    public static Selector Tag(string tagName) => new(tagName);

    public Selector WithId(string value) => WithAttribute("id", value);

    public Selector WithClass(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Any(IsHtmlSpace))
            throw new ArgumentException("A class-token predicate must contain exactly one token.", nameof(token));
        _attributes.Add(
            new AttributePredicate(NormalizeName("class", "name"), AttributePredicateKind.ContainsToken, token)
        );
        return this;
    }

    public Selector WithAttribute(string name)
    {
        _attributes.Add(new AttributePredicate(NormalizeName(name, nameof(name)), AttributePredicateKind.Exists, null));
        return this;
    }

    public Selector WithAttribute(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _attributes.Add(
            new AttributePredicate(NormalizeName(name, nameof(name)), AttributePredicateKind.Equals, value)
        );
        return this;
    }

    internal static string NormalizeName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        foreach (var character in value)
        {
            if (character > 0x7F)
                throw new NotSupportedException(
                    "The streaming query prototype accepts ASCII tag and attribute names only."
                );
        }
        return value.ToLowerInvariant();
    }

    private static bool IsHtmlSpace(char value) => value is ' ' or '\t' or '\n' or '\r' or '\f';
}

internal enum AttributePredicateKind : byte
{
    Exists,
    Equals,
    ContainsToken,
}

internal sealed record AttributePredicate(string Name, AttributePredicateKind Kind, string? Value);

public sealed class QueryNode<TState>
{
    private readonly QueryNode<TState> _root;
    private readonly List<QueryNode<TState>> _children = [];
    private readonly HashSet<string> _projectedAttributes = new(StringComparer.Ordinal);
    private StartHandler<TState>? _start;
    private TextHandler<TState>? _text;
    private EndHandler<TState>? _end;
    private CompletedElementHandler<TState>? _completed;
    private CompletedTextMode _completedTextMode;

    private QueryNode(Selector selector, QueryRelation relation, QueryNode<TState>? parent)
    {
        Selector = selector ?? throw new ArgumentNullException(nameof(selector));
        Relation = relation;
        Parent = parent;
        _root = parent?._root ?? this;
    }

    public Selector Selector { get; }
    public QueryRelation Relation { get; }
    public QueryNode<TState>? Parent { get; }

    internal IReadOnlyList<QueryNode<TState>> Children => _children;
    internal IReadOnlyCollection<string> ProjectedAttributes => _projectedAttributes;
    internal StartHandler<TState>? StartHandler => _start;
    internal TextHandler<TState>? TextHandler => _text;
    internal EndHandler<TState>? EndHandler => _end;
    internal CompletedElementHandler<TState>? CompletedHandler => _completed;
    internal CompletedTextMode CompletedTextMode => _completedTextMode;

    public static QueryNode<TState> Root(Selector selector) => new(selector, QueryRelation.Root, null);

    public static QueryNode<TState> Root(string tagName) => Root(Selector.Tag(tagName));

    public QueryNode<TState> Descendant(Selector selector) => AddChild(selector, QueryRelation.Descendant);

    public QueryNode<TState> Descendant(string tagName) => Descendant(Selector.Tag(tagName));

    public QueryNode<TState> Child(Selector selector) => AddChild(selector, QueryRelation.Child);

    public QueryNode<TState> Child(string tagName) => Child(Selector.Tag(tagName));

    public QueryNode<TState> WithId(string value)
    {
        Selector.WithId(value);
        return this;
    }

    public QueryNode<TState> Id(string value) => WithId(value);

    public QueryNode<TState> WithClass(string token)
    {
        Selector.WithClass(token);
        return this;
    }

    public QueryNode<TState> Class(string token) => WithClass(token);

    public QueryNode<TState> WithAttribute(string name)
    {
        Selector.WithAttribute(name);
        return this;
    }

    public QueryNode<TState> Attribute(string name) => WithAttribute(name);

    public QueryNode<TState> WithAttribute(string name, string value)
    {
        Selector.WithAttribute(name, value);
        return this;
    }

    public QueryNode<TState> Attribute(string name, string value) => WithAttribute(name, value);

    public QueryNode<TState> OnStart(StartHandler<TState> handler, params string[] projectedAttributes)
    {
        EnsureLowLevelHandlersCanBeAdded();
        _start = handler ?? throw new ArgumentNullException(nameof(handler));
        foreach (var attribute in projectedAttributes)
            _projectedAttributes.Add(Selector.NormalizeName(attribute, nameof(projectedAttributes)));
        return this;
    }

    public QueryNode<TState> OnText(TextHandler<TState> handler)
    {
        EnsureLowLevelHandlersCanBeAdded();
        _text = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    public QueryNode<TState> OnEnd(EndHandler<TState> handler)
    {
        EnsureLowLevelHandlersCanBeAdded();
        _end = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    public QueryNode<TState> OnClose(CompletedElementHandler<TState> handler, params string[] projectedAttributes) =>
        OnCompleted(CompletedTextMode.None, handler, projectedAttributes);

    public QueryNode<TState> OnTextContent(
        CompletedElementHandler<TState> handler,
        params string[] projectedAttributes
    ) => OnCompleted(CompletedTextMode.Raw, handler, projectedAttributes);

    public QueryNode<TState> OnNormalizedText(
        CompletedElementHandler<TState> handler,
        params string[] projectedAttributes
    ) => OnCompleted(CompletedTextMode.Normalized, handler, projectedAttributes);

    public QueryPlan<TState> Compile() => QueryCompiler.Compile(_root);

    private QueryNode<TState> AddChild(Selector selector, QueryRelation relation)
    {
        var child = new QueryNode<TState>(selector, relation, this);
        _children.Add(child);
        return child;
    }

    private QueryNode<TState> OnCompleted(
        CompletedTextMode textMode,
        CompletedElementHandler<TState> handler,
        string[] projectedAttributes
    )
    {
        if (_start is not null || _text is not null || _end is not null)
            throw new InvalidOperationException(
                "A completed-element callback cannot be combined with start, text, or end callbacks on the same query node."
            );
        if (_completed is not null)
            throw new InvalidOperationException("A query node can have only one completed-element callback.");
        _completed = handler ?? throw new ArgumentNullException(nameof(handler));
        _completedTextMode = textMode;
        foreach (var attribute in projectedAttributes)
            _projectedAttributes.Add(Selector.NormalizeName(attribute, nameof(projectedAttributes)));
        return this;
    }

    private void EnsureLowLevelHandlersCanBeAdded()
    {
        if (_completed is not null)
            throw new InvalidOperationException(
                "Start, text, and end callbacks cannot be combined with a completed-element callback on the same query node."
            );
    }
}

public static class StreamQuery
{
    public static QueryNode<TState> For<TState>(string rootTag) => QueryNode<TState>.Root(rootTag);
}

public sealed record QueryExplanation(
    string ExecutionShape,
    IReadOnlyList<string> RequiredTags,
    IReadOnlyList<string> RequiredAttributes,
    int QueryNodes,
    int EstimatedFrameBytes,
    bool CanStopAfterRoot,
    string? FailureReason
);

public sealed class QueryPlan<TState>
{
    internal QueryPlan(
        CompiledQueryNode<TState>[] nodes,
        string[] attributeNames,
        byte[][] attributeNameUtf8,
        QueryExplanation explanation
    )
    {
        Nodes = nodes;
        AttributeNames = attributeNames;
        AttributeNameUtf8 = attributeNameUtf8;
        TextHandlerBits = nodes.Aggregate(
            0UL,
            static (bits, node) => node.Text is null ? bits : bits | (1UL << node.Index)
        );
        CompletedHandlerBits = nodes.Aggregate(
            0UL,
            static (bits, node) => node.Completed is null ? bits : bits | (1UL << node.Index)
        );
        Explanation = explanation;
    }

    internal CompiledQueryNode<TState>[] Nodes { get; }
    internal string[] AttributeNames { get; }
    internal byte[][] AttributeNameUtf8 { get; }
    internal ulong TextHandlerBits { get; }
    internal ulong CompletedHandlerBits { get; }

    public QueryExplanation Explanation { get; }

    public QuerySession<TState> CreateSession(TState state) => new(this, state);

    public TState Execute(ReadOnlySpan<byte> utf8, TState state)
    {
        using var session = CreateSession(state);
        var tokenizer = new Utf8HtmlTokenizer(session);
        tokenizer.Write(utf8);
        tokenizer.Complete();
        return session.State;
    }

    public async ValueTask<TState> ExecuteAsync(
        PipeReader reader,
        TState state,
        CancellationToken cancellationToken = default
    )
    {
        using var session = CreateSession(state);
        await Utf8HtmlTokenizer.TokenizeAsync(reader, session, cancellationToken).ConfigureAwait(false);
        return session.State;
    }
}

internal sealed record CompiledQueryNode<TState>(
    int Index,
    int ParentIndex,
    QueryRelation Relation,
    byte[] TagName,
    ulong TagHash,
    ulong RequiredAttributeBits,
    CompiledAttributePredicate[] Predicates,
    StartHandler<TState>? Start,
    TextHandler<TState>? Text,
    EndHandler<TState>? End,
    CompletedElementHandler<TState>? Completed,
    CompletedTextMode CompletedTextMode,
    int[] CompletedAttributeIndexes
);

internal readonly record struct CompiledAttributePredicate(
    int AttributeIndex,
    AttributePredicateKind Kind,
    byte[]? Value
);

internal static class QueryCompiler
{
    public static QueryPlan<TState> Compile<TState>(QueryNode<TState> root)
    {
        var sourceNodes = new List<QueryNode<TState>>();
        AddPreorder(root, sourceNodes);
        if (sourceNodes.Count > 64)
            throw new NotSupportedException("StreamingOnly plans support at most 64 query nodes.");

        var attributeNames = sourceNodes
            .SelectMany(static node =>
                node.Selector.Attributes.Select(static predicate => predicate.Name).Concat(node.ProjectedAttributes)
            )
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var attributeNameUtf8 = attributeNames.Select(Encoding.UTF8.GetBytes).ToArray();
        if (attributeNames.Length > 64)
            throw new NotSupportedException("StreamingOnly plans support at most 64 required attributes.");
        var attributeIndexes = attributeNames
            .Select(static (name, index) => (name, index))
            .ToDictionary(static pair => pair.name, static pair => pair.index, StringComparer.Ordinal);
        var sourceIndexes = sourceNodes
            .Select(static (node, index) => (node, index))
            .ToDictionary(static pair => pair.node, static pair => pair.index);

        var nodes = new CompiledQueryNode<TState>[sourceNodes.Count];
        for (var index = 0; index < sourceNodes.Count; index++)
        {
            var source = sourceNodes[index];
            var tagName = Encoding.UTF8.GetBytes(source.Selector.TagName);
            var predicates = source
                .Selector.Attributes.Select(predicate => new CompiledAttributePredicate(
                    attributeIndexes[predicate.Name],
                    predicate.Kind,
                    predicate.Value is null ? null : Encoding.UTF8.GetBytes(predicate.Value)
                ))
                .ToArray();
            var requiredAttributeBits = source
                .Selector.Attributes.Select(static predicate => predicate.Name)
                .Concat(source.ProjectedAttributes)
                .Aggregate(0UL, (bits, name) => bits | (1UL << attributeIndexes[name]));
            var completedAttributeNames = source.CompletedHandler is null
                ? []
                : source
                    .Selector.Attributes.Select(static predicate => predicate.Name)
                    .Concat(source.ProjectedAttributes)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            nodes[index] = new CompiledQueryNode<TState>(
                index,
                source.Parent is null ? -1 : sourceIndexes[source.Parent],
                source.Relation,
                tagName,
                Hash(tagName),
                requiredAttributeBits,
                predicates,
                source.StartHandler,
                source.TextHandler,
                source.EndHandler,
                source.CompletedHandler,
                source.CompletedTextMode,
                completedAttributeNames.Select(name => attributeIndexes[name]).ToArray()
            );
        }

        var tags = nodes
            .Select(static node => Encoding.UTF8.GetString(node.TagName))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var explanation = new QueryExplanation("StreamingOnly", tags, attributeNames, nodes.Length, 24, false, null);
        return new QueryPlan<TState>(nodes, attributeNames, attributeNameUtf8, explanation);
    }

    private static void AddPreorder<TState>(QueryNode<TState> node, List<QueryNode<TState>> nodes)
    {
        nodes.Add(node);
        foreach (var child in node.Children)
            AddPreorder(child, nodes);
    }

    internal static ulong Hash(ReadOnlySpan<byte> value)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }
        return hash;
    }
}

internal static class VoidElementHashes
{
    internal static readonly ulong AreaHash = QueryCompiler.Hash("area"u8);
    internal static readonly ulong BaseHash = QueryCompiler.Hash("base"u8);
    internal static readonly ulong BrHash = QueryCompiler.Hash("br"u8);
    internal static readonly ulong ColHash = QueryCompiler.Hash("col"u8);
    internal static readonly ulong EmbedHash = QueryCompiler.Hash("embed"u8);
    internal static readonly ulong HrHash = QueryCompiler.Hash("hr"u8);
    internal static readonly ulong ImgHash = QueryCompiler.Hash("img"u8);
    internal static readonly ulong InputHash = QueryCompiler.Hash("input"u8);
    internal static readonly ulong LinkHash = QueryCompiler.Hash("link"u8);
    internal static readonly ulong MetaHash = QueryCompiler.Hash("meta"u8);
    internal static readonly ulong ParamHash = QueryCompiler.Hash("param"u8);
    internal static readonly ulong SourceHash = QueryCompiler.Hash("source"u8);
    internal static readonly ulong TrackHash = QueryCompiler.Hash("track"u8);
    internal static readonly ulong WbrHash = QueryCompiler.Hash("wbr"u8);
}

public sealed class QuerySession<TState> : IUtf8HtmlTokenSink, IDisposable
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

    internal QuerySession(QueryPlan<TState> plan, TState state)
    {
        _plan = plan;
        _state = state;
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

    public void StartTag(ReadOnlySpan<byte> name)
    {
        _pendingTagHash = QueryCompiler.Hash(name);
        _pendingTagLength = name.Length;
        _pendingCandidateBits = 0;
        _pendingAttributeBits = 0;
        foreach (var node in _plan.Nodes)
        {
            if (
                node.TagHash != _pendingTagHash
                || node.TagName.Length != _pendingTagLength
                || !name.SequenceEqual(node.TagName)
                || !ParentMatches(node)
            )
                continue;
            _pendingCandidateBits |= 1UL << node.Index;
            _pendingAttributeBits |= node.RequiredAttributeBits;
        }
        ResetAttributes();
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
            EnsureAttributeCapacity(value.Length);
            _attributeStarts[index] = _attributeValueLength;
            _attributeLengths[index] = value.Length;
            _seenAttributeBits |= 1UL << index;
            value.CopyTo(_attributeValues.AsSpan(_attributeValueLength));
            _attributeValueLength += value.Length;
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

        var closesImmediately = selfClosing || IsVoidTag(_pendingTagHash, _pendingTagLength);
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

    public void EndTag(ReadOnlySpan<byte> name)
    {
        var hash = QueryCompiler.Hash(name);
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
                    capture.SetAttribute(
                        attribute,
                        _attributeValues.AsSpan(_attributeStarts[attributeIndex], length)
                    );
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
                capture.Append(utf8);
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

internal readonly record struct QueryFrame(ulong TagHash, int TagLength, ulong Matches);

internal sealed class ElementCapture : IDisposable
{
    private byte[] _utf8 = ArrayPool<byte>.Shared.Rent(256);
    private int[] _attributeStarts = ArrayPool<int>.Shared.Rent(1);
    private int[] _attributeLengths = ArrayPool<int>.Shared.Rent(1);
    private CompletedTextMode _textMode;
    private int _length;
    private int _textStart;
    private string? _decodedText;
    private bool _pendingSpace;
    private bool _disposed;

    internal ReadOnlySpan<byte> TextUtf8 => _utf8.AsSpan(_textStart, _length - _textStart);

    internal void Reset(CompletedTextMode textMode, int attributeCount)
    {
        _textMode = textMode;
        _length = 0;
        _textStart = 0;
        _decodedText = null;
        _pendingSpace = false;
        EnsureAttributeCapacity(attributeCount);
        _attributeLengths.AsSpan(0, attributeCount).Fill(-1);
    }

    internal void SetAttribute(int index, ReadOnlySpan<byte> value)
    {
        EnsureUtf8Capacity(value.Length);
        _attributeStarts[index] = _length;
        _attributeLengths[index] = value.Length;
        value.CopyTo(_utf8.AsSpan(_length));
        _length += value.Length;
    }

    internal void BeginText() => _textStart = _length;

    internal bool TryGetAttribute(int index, out ReadOnlySpan<byte> value)
    {
        var length = _attributeLengths[index];
        if (length < 0)
        {
            value = default;
            return false;
        }
        value = _utf8.AsSpan(_attributeStarts[index], length);
        return true;
    }

    internal void Append(ReadOnlySpan<byte> utf8)
    {
        if (_textMode == CompletedTextMode.None)
            return;
        if (_textMode == CompletedTextMode.Raw)
        {
            AppendBytes(utf8);
            return;
        }
        while (!utf8.IsEmpty)
        {
            var status = Rune.DecodeFromUtf8(utf8, out var rune, out var consumed);
            if (status != OperationStatus.Done)
                throw new InvalidOperationException("The tokenizer emitted incomplete UTF-8 text.");
            var scalar = utf8[..consumed];
            utf8 = utf8[consumed..];
            if (Rune.IsWhiteSpace(rune))
            {
                _pendingSpace = _length != _textStart;
                continue;
            }
            if (_pendingSpace)
            {
                AppendByte((byte)' ');
                _pendingSpace = false;
            }
            AppendBytes(scalar);
        }
    }

    internal string GetText() => _decodedText ??= Encoding.UTF8.GetString(TextUtf8);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ArrayPool<byte>.Shared.Return(_utf8);
        ArrayPool<int>.Shared.Return(_attributeStarts, clearArray: true);
        ArrayPool<int>.Shared.Return(_attributeLengths, clearArray: true);
        _utf8 = [];
        _attributeStarts = [];
        _attributeLengths = [];
    }

    private void AppendByte(byte value)
    {
        EnsureUtf8Capacity(1);
        _utf8[_length++] = value;
    }

    private void AppendBytes(ReadOnlySpan<byte> value)
    {
        EnsureUtf8Capacity(value.Length);
        value.CopyTo(_utf8.AsSpan(_length));
        _length += value.Length;
    }

    private void EnsureUtf8Capacity(int additional)
    {
        if (_length + additional <= _utf8.Length)
            return;
        var replacement = ArrayPool<byte>.Shared.Rent(Math.Max(_utf8.Length * 2, _length + additional));
        _utf8.AsSpan(0, _length).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(_utf8);
        _utf8 = replacement;
    }

    private void EnsureAttributeCapacity(int count)
    {
        if (count <= _attributeStarts.Length)
            return;
        ArrayPool<int>.Shared.Return(_attributeStarts, clearArray: true);
        ArrayPool<int>.Shared.Return(_attributeLengths, clearArray: true);
        _attributeStarts = ArrayPool<int>.Shared.Rent(count);
        _attributeLengths = ArrayPool<int>.Shared.Rent(count);
    }
}
