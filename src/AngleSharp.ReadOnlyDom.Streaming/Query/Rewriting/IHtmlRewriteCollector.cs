namespace AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;

internal interface IHtmlRewriteCollector
{
    bool NeedsEndTagSourceRanges { get; }

    bool IsSuppressingContent { get; }

    int BeginElement(long sourceStart, long sourceEnd, bool canHaveContent, bool selfClosingSyntax);

    void CommitElement(int scopeId);

    void EndElement(int scopeId, long sourceStart, long sourceEnd, bool hasExplicitEndTag);

    void AppendAttribute(int scopeId, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value);

    void SetAttribute(int scopeId, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value);

    void RemoveAttribute(int scopeId, ReadOnlySpan<byte> name);

    void Before(int scopeId, ReadOnlySpan<byte> content, HtmlRewriteContentType contentType);

    void After(int scopeId, ReadOnlySpan<byte> content, HtmlRewriteContentType contentType);

    void Prepend(int scopeId, ReadOnlySpan<byte> content, HtmlRewriteContentType contentType);

    void Append(int scopeId, ReadOnlySpan<byte> content, HtmlRewriteContentType contentType);

    void SetInnerContent(int scopeId, ReadOnlySpan<byte> content, HtmlRewriteContentType contentType);

    void Replace(int scopeId, ReadOnlySpan<byte> content, HtmlRewriteContentType contentType);

    void Remove(int scopeId);

    void RemoveAndKeepContent(int scopeId);

    int BeginText(long sourceStart, long sourceEnd);

    void CommitText(int scopeId);

    void TextBefore(int scopeId, ReadOnlySpan<byte> content, HtmlRewriteContentType contentType);

    void TextAfter(int scopeId, ReadOnlySpan<byte> content, HtmlRewriteContentType contentType);

    void ReplaceText(int scopeId, ReadOnlySpan<byte> content, HtmlRewriteContentType contentType);

    void RemoveText(int scopeId);
}
