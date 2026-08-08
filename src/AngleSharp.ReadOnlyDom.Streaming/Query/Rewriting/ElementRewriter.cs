namespace AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;

/// <summary>
/// Records mutations for the currently matched element. All supplied spans are copied before the
/// callback returns. Mutations are applied to the original byte stream without constructing a DOM.
/// </summary>
public ref struct ElementRewriter
{
    private readonly IHtmlRewriteCollector _collector;
    private readonly long _sourceStart;
    private readonly long _sourceEnd;
    private readonly bool _canHaveContent;
    private readonly bool _selfClosingSyntax;
    private int _scopeId;

    internal ElementRewriter(
        IHtmlRewriteCollector collector,
        long sourceStart,
        long sourceEnd,
        bool canHaveContent,
        bool selfClosingSyntax
    )
    {
        _collector = collector;
        _sourceStart = sourceStart;
        _sourceEnd = sourceEnd;
        _canHaveContent = canHaveContent;
        _selfClosingSyntax = selfClosingSyntax;
        _scopeId = -1;
    }

    internal readonly int ScopeId => _scopeId;

    internal readonly void Commit() => _collector.CommitElement(_scopeId);

    /// <summary>
    /// Appends an attribute immediately before the tag close. Unlike <see cref="SetAttribute"/>,
    /// this method does not inspect or replace an existing attribute with the same name.
    /// </summary>
    public void AppendAttribute(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value) =>
        _collector.AppendAttribute(EnsureScope(), name, value);

    /// <summary>Replaces an existing attribute case-insensitively, or appends it when absent.</summary>
    public void SetAttribute(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value) =>
        _collector.SetAttribute(EnsureScope(), name, value);

    /// <summary>Removes every occurrence of an attribute case-insensitively.</summary>
    public void RemoveAttribute(ReadOnlySpan<byte> name) => _collector.RemoveAttribute(EnsureScope(), name);

    /// <summary>Inserts content immediately before the element.</summary>
    public void Before(ReadOnlySpan<byte> content, HtmlRewriteContentType contentType) =>
        _collector.Before(EnsureScope(), content, contentType);

    /// <summary>Inserts content immediately after the element.</summary>
    public void After(ReadOnlySpan<byte> content, HtmlRewriteContentType contentType) =>
        _collector.After(EnsureScope(), content, contentType);

    /// <summary>Inserts content immediately after the start tag. No-op for HTML void elements.</summary>
    public void Prepend(ReadOnlySpan<byte> content, HtmlRewriteContentType contentType)
    {
        if (_canHaveContent)
            _collector.Prepend(EnsureScope(), content, contentType);
    }

    /// <summary>Inserts content immediately before the end tag. No-op for HTML void elements.</summary>
    public void Append(ReadOnlySpan<byte> content, HtmlRewriteContentType contentType)
    {
        if (_canHaveContent)
            _collector.Append(EnsureScope(), content, contentType);
    }

    /// <summary>Replaces the element's descendants. No-op for HTML void elements.</summary>
    public void SetInnerContent(ReadOnlySpan<byte> content, HtmlRewriteContentType contentType)
    {
        if (_canHaveContent)
            _collector.SetInnerContent(EnsureScope(), content, contentType);
    }

    /// <summary>Replaces the element, including its tags and descendants.</summary>
    public void Replace(ReadOnlySpan<byte> content, HtmlRewriteContentType contentType) =>
        _collector.Replace(EnsureScope(), content, contentType);

    /// <summary>Removes the element, including its descendants.</summary>
    public void Remove() => _collector.Remove(EnsureScope());

    /// <summary>Removes the element's start and end tags while preserving its descendants.</summary>
    public void RemoveAndKeepContent() => _collector.RemoveAndKeepContent(EnsureScope());

    private int EnsureScope()
    {
        if (_scopeId < 0)
        {
            _scopeId = _collector.BeginElement(_sourceStart, _sourceEnd, _canHaveContent, _selfClosingSyntax);
        }
        return _scopeId;
    }
}
