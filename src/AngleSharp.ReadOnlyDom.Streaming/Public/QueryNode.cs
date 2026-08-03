using AngleSharp.ReadOnlyDom.Streaming.Internal;

namespace AngleSharp.ReadOnlyDom.Streaming.Public;

/// <summary>
/// Defines a mutable query over the token stream's lexical start/end-tag stack. It does not represent
/// browser-corrected HTML tree topology.
/// </summary>
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

    internal Selector Selector { get; }
    internal QueryRelation Relation { get; }
    internal QueryNode<TState>? Parent { get; }

    internal IReadOnlyList<QueryNode<TState>> Children => _children;
    internal IReadOnlyCollection<string> ProjectedAttributes => _projectedAttributes;
    internal StartHandler<TState>? StartHandler => _start;
    internal TextHandler<TState>? TextHandler => _text;
    internal EndHandler<TState>? EndHandler => _end;
    internal CompletedElementHandler<TState>? CompletedHandler => _completed;
    internal CompletedTextMode CompletedTextMode => _completedTextMode;
    internal QueryNode<TState> RootNode => _root;

    internal static QueryNode<TState> Root(Selector selector) => new(selector, QueryRelation.Root, null);

    internal static QueryNode<TState> Root(string tagName) => Root(Selector.Tag(tagName));

    internal QueryNode<TState> Descendant(Selector selector) => AddChild(selector, QueryRelation.Descendant);

    /// <summary>Adds a query whose tag must have this node in its active lexical ancestor stack.</summary>
    public QueryNode<TState> Descendant(string tagName) => Descendant(Selector.Tag(tagName));

    internal QueryNode<TState> Child(Selector selector) => AddChild(selector, QueryRelation.Child);

    /// <summary>Adds a query whose tag must be immediately enclosed by this node's lexical frame.</summary>
    public QueryNode<TState> Child(string tagName) => Child(Selector.Tag(tagName));

    public QueryNode<TState> Id(string value)
    {
        Selector.WithId(value);
        return this;
    }

    public QueryNode<TState> Class(string token)
    {
        Selector.WithClass(token);
        return this;
    }

    public QueryNode<TState> Attribute(string name)
    {
        Selector.WithAttribute(name);
        return this;
    }

    public QueryNode<TState> Attribute(string name, string value)
    {
        Selector.WithAttribute(name, value);
        return this;
    }

    public QueryNode<TState> OnStart(StartHandler<TState> handler, params string[] projectedAttributes)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(projectedAttributes);
        EnsureLowLevelHandlerCanBeAdded(_start, "start");
        var normalizedAttributes = NormalizeProjectedAttributes(projectedAttributes);
        _start = handler;
        _projectedAttributes.UnionWith(normalizedAttributes);
        return this;
    }

    public QueryNode<TState> OnText(TextHandler<TState> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureLowLevelHandlerCanBeAdded(_text, "text");
        _text = handler;
        return this;
    }

    public QueryNode<TState> OnEnd(EndHandler<TState> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureLowLevelHandlerCanBeAdded(_end, "end");
        _end = handler;
        return this;
    }

    public QueryNode<TState> OnClose(CompletedElementHandler<TState> handler, params string[] projectedAttributes) =>
        OnCompleted(CompletedTextMode.None, handler, projectedAttributes);

    public QueryNode<TState> OnTextContent(
        CompletedElementHandler<TState> handler,
        params string[] projectedAttributes
    ) => OnCompleted(CompletedTextMode.Raw, handler, projectedAttributes);

    /// <summary>
    /// Invokes <paramref name="handler"/> with runs of HTML ASCII whitespace and NBSP collapsed
    /// to a single ASCII space. Other Unicode whitespace is preserved.
    /// </summary>
    public QueryNode<TState> OnNormalizedText(
        CompletedElementHandler<TState> handler,
        params string[] projectedAttributes
    ) => OnCompleted(CompletedTextMode.Normalized, handler, projectedAttributes);

    public QueryPlan<TState> Compile() => QueryPlanCompiler.Compile(_root);

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
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(projectedAttributes);
        if (_start is not null || _text is not null || _end is not null)
            throw new InvalidOperationException(
                "A completed-element callback cannot be combined with start, text, or end callbacks on the same query node."
            );
        if (_completed is not null)
            throw new InvalidOperationException("A query node can have only one completed-element callback.");
        var normalizedAttributes = NormalizeProjectedAttributes(projectedAttributes);
        _completed = handler;
        _completedTextMode = textMode;
        _projectedAttributes.UnionWith(normalizedAttributes);
        return this;
    }

    private static string[] NormalizeProjectedAttributes(string[] projectedAttributes) =>
        projectedAttributes
            .Select(attribute => Selector.NormalizeAttributeName(attribute, nameof(projectedAttributes)))
            .ToArray();

    private void EnsureLowLevelHandlerCanBeAdded(Delegate? existingHandler, string handlerName)
    {
        if (_completed is not null)
            throw new InvalidOperationException(
                "Start, text, and end callbacks cannot be combined with a completed-element callback on the same query node."
            );
        if (existingHandler is not null)
            throw new InvalidOperationException($"A query node can have only one {handlerName} callback.");
    }
}
