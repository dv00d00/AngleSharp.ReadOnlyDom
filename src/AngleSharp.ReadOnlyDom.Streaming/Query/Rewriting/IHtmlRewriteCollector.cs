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

internal abstract class HtmlRewriteCollectorBase : IHtmlRewriteCollector
{
    private int _endTagRangeScopes;
    private int _suppressionDepth;
    private int _sequence;

    protected List<HtmlElementMutation> Mutations { get; } = [];
    protected List<HtmlTextMutation> TextMutations { get; } = [];

    public bool NeedsEndTagSourceRanges => _endTagRangeScopes != 0;

    public bool IsSuppressingContent => _suppressionDepth != 0;

    public int BeginElement(long sourceStart, long sourceEnd, bool canHaveContent, bool selfClosingSyntax)
    {
        var mutation = new HtmlElementMutation(sourceStart, sourceEnd, canHaveContent, selfClosingSyntax);
        Mutations.Add(mutation);
        return Mutations.Count - 1;
    }

    public virtual void CommitElement(int scopeId)
    {
        if (scopeId < 0)
            return;
        var mutation = Mutation(scopeId);
        mutation.Ignored = _suppressionDepth != 0;
        if (mutation.Ignored)
            return;
        mutation.StartSequence = _sequence++;
        mutation.RequiresEndTagRange =
            mutation.CanHaveContent
            && (
                mutation.Disposition
                    is ElementDisposition.Remove
                        or ElementDisposition.Replace
                        or ElementDisposition.Unwrap
                || mutation.SuppressInnerContent
                || mutation.Append.Count != 0
                || mutation.After.Count != 0
            );
        if (mutation.RequiresEndTagRange)
            _endTagRangeScopes++;
        mutation.OpensSuppression =
            mutation.CanHaveContent
            && (
                mutation.Disposition is ElementDisposition.Remove or ElementDisposition.Replace
                || mutation.SuppressInnerContent
            );
        if (mutation.OpensSuppression)
            _suppressionDepth++;
    }

    public virtual void EndElement(int scopeId, long sourceStart, long sourceEnd, bool hasExplicitEndTag)
    {
        if (scopeId < 0)
            return;
        var mutation = Mutations[scopeId];
        if (mutation.Ignored)
            return;
        if (mutation.RequiresEndTagRange)
            _endTagRangeScopes--;
        mutation.EndStart = sourceStart;
        mutation.EndEnd = sourceEnd;
        mutation.HasExplicitEndTag = hasExplicitEndTag;
        mutation.EndSequence = _sequence++;
        if (mutation.OpensSuppression)
            _suppressionDepth--;
    }

    public void AppendAttribute(int scopeId, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        HtmlRewritePayload.ValidateAttributeName(name);
        Mutation(scopeId)
            .Attributes.Add(
                new AttributeMutation(
                    AttributeMutationKind.Append,
                    name.ToArray(),
                    HtmlRewritePayload.CopyAttributeValue(value)
                )
            );
    }

    public void SetAttribute(int scopeId, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        HtmlRewritePayload.ValidateAttributeName(name);
        Mutation(scopeId)
            .Attributes.Add(
                new AttributeMutation(
                    AttributeMutationKind.Set,
                    name.ToArray(),
                    HtmlRewritePayload.CopyAttributeValue(value)
                )
            );
    }

    public void RemoveAttribute(int scopeId, ReadOnlySpan<byte> name)
    {
        HtmlRewritePayload.ValidateAttributeName(name);
        Mutation(scopeId).Attributes.Add(new AttributeMutation(AttributeMutationKind.Remove, name.ToArray(), null));
    }

    public void Before(int scopeId, ReadOnlySpan<byte> content, HtmlRewriteContentType contentType) =>
        Mutation(scopeId).Before.Add(HtmlRewritePayload.CopyContent(content, contentType));

    public void After(int scopeId, ReadOnlySpan<byte> content, HtmlRewriteContentType contentType) =>
        Mutation(scopeId).After.Add(HtmlRewritePayload.CopyContent(content, contentType));

    public void Prepend(int scopeId, ReadOnlySpan<byte> content, HtmlRewriteContentType contentType) =>
        Mutation(scopeId).Prepend.Add(HtmlRewritePayload.CopyContent(content, contentType));

    public void Append(int scopeId, ReadOnlySpan<byte> content, HtmlRewriteContentType contentType) =>
        Mutation(scopeId).Append.Add(HtmlRewritePayload.CopyContent(content, contentType));

    public void SetInnerContent(int scopeId, ReadOnlySpan<byte> content, HtmlRewriteContentType contentType)
    {
        var mutation = Mutation(scopeId);
        mutation.Prepend.Clear();
        mutation.Append.Clear();
        mutation.InnerReplacement = HtmlRewritePayload.CopyContent(content, contentType);
        mutation.SuppressInnerContent = true;
    }

    public void Replace(int scopeId, ReadOnlySpan<byte> content, HtmlRewriteContentType contentType)
    {
        var mutation = Mutation(scopeId);
        mutation.Disposition = ElementDisposition.Replace;
        mutation.Replacement = HtmlRewritePayload.CopyContent(content, contentType);
        mutation.SuppressInnerContent = mutation.CanHaveContent;
    }

    public void Remove(int scopeId)
    {
        var mutation = Mutation(scopeId);
        mutation.Disposition = ElementDisposition.Remove;
        mutation.Replacement = null;
        mutation.SuppressInnerContent = mutation.CanHaveContent;
    }

    public void RemoveAndKeepContent(int scopeId)
    {
        var mutation = Mutation(scopeId);
        mutation.Disposition = ElementDisposition.Unwrap;
        mutation.Replacement = null;
        mutation.SuppressInnerContent = false;
        mutation.InnerReplacement = null;
    }

    public int BeginText(long sourceStart, long sourceEnd)
    {
        TextMutations.Add(new HtmlTextMutation(sourceStart, sourceEnd));
        return TextMutations.Count - 1;
    }

    public virtual void CommitText(int scopeId)
    {
        if (scopeId >= 0)
            TextMutation(scopeId).Sequence = _sequence++;
    }

    public void TextBefore(int scopeId, ReadOnlySpan<byte> content, HtmlRewriteContentType contentType) =>
        TextMutation(scopeId).Before.Add(HtmlRewritePayload.CopyContent(content, contentType));

    public void TextAfter(int scopeId, ReadOnlySpan<byte> content, HtmlRewriteContentType contentType) =>
        TextMutation(scopeId).After.Add(HtmlRewritePayload.CopyContent(content, contentType));

    public void ReplaceText(int scopeId, ReadOnlySpan<byte> content, HtmlRewriteContentType contentType)
    {
        var mutation = TextMutation(scopeId);
        mutation.Replacement = HtmlRewritePayload.CopyContent(content, contentType);
        mutation.Removed = true;
    }

    public void RemoveText(int scopeId)
    {
        var mutation = TextMutation(scopeId);
        mutation.Replacement = null;
        mutation.Removed = true;
    }

    protected HtmlElementMutation Mutation(int scopeId) => Mutations[scopeId];

    protected HtmlTextMutation TextMutation(int scopeId) => TextMutations[scopeId];
}
