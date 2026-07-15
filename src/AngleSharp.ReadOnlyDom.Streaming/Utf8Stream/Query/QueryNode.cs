namespace AngleSharp.ReadOnlyDom.Streaming.Utf8Stream.Query;

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