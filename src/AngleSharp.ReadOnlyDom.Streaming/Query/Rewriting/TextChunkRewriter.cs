namespace AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;

/// <summary>Records mutations for one raw text fragment.</summary>
public ref struct TextChunkRewriter
{
    private readonly IHtmlRewriteCollector _collector;
    private readonly long _sourceStart;
    private readonly long _sourceEnd;
    private int _scopeId;

    internal TextChunkRewriter(IHtmlRewriteCollector collector, long sourceStart, long sourceEnd)
    {
        _collector = collector;
        _sourceStart = sourceStart;
        _sourceEnd = sourceEnd;
        _scopeId = -1;
    }

    internal readonly void Commit() => _collector.CommitText(_scopeId);

    /// <summary>Inserts content before this fragment. Consecutive calls preserve call order.</summary>
    public void Before(ReadOnlySpan<byte> content, HtmlRewriteContentType contentType) =>
        _collector.TextBefore(EnsureScope(), content, contentType);

    /// <summary>Inserts content after this fragment. Consecutive calls use reverse call order.</summary>
    public void After(ReadOnlySpan<byte> content, HtmlRewriteContentType contentType) =>
        _collector.TextAfter(EnsureScope(), content, contentType);

    /// <summary>Replaces this fragment; a later replacement wins.</summary>
    public void Replace(ReadOnlySpan<byte> content, HtmlRewriteContentType contentType) =>
        _collector.ReplaceText(EnsureScope(), content, contentType);

    /// <summary>Removes this fragment.</summary>
    public void Remove() => _collector.RemoveText(EnsureScope());

    private int EnsureScope()
    {
        if (_scopeId < 0)
            _scopeId = _collector.BeginText(_sourceStart, _sourceEnd);
        return _scopeId;
    }
}
