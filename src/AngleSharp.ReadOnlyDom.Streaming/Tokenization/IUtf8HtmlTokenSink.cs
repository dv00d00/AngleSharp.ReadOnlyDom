#pragma warning disable CS1591 // Experimental API surface; shape is intentionally unsettled.

namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

[Flags]
internal enum Utf8HtmlStartTagCapture : byte
{
    None = 0,
    Attributes = 1,
}

[Flags]
internal enum Utf8HtmlTokenCapture : byte
{
    None = 0,
    Text = 1,
}

internal enum Utf8HtmlTextType : byte
{
    Data,
    RcData,
    RawText,
    ScriptData,
    PlainText,
    CDataSection,
}

/// <summary>
/// Synchronous borrowed views over tokenizer-owned or PipeReader-owned UTF-8. Every span is valid only for the duration
/// of its callback. The split start-tag callbacks let a construction sink collect only attributes it needs.
/// </summary>
internal interface IUtf8HtmlTokenSink
{
    Utf8HtmlTokenCapture Capture { get; }

    void Text(ReadOnlySpan<Byte> utf8);

    Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name);

    /// <summary>
    /// Consumes one captured attribute value as raw buffered bytes: character references are NOT
    /// decoded. When <paramref name="valueMayContainReferences"/> is <see langword="true"/> and the
    /// value contains '&amp;', a sink that needs the decoded form runs
    /// <see cref="Utf8AttributeValueDecoder.Decode"/> over it — ideally lazily, on first read, since
    /// most captured values are never observed. When the flag is <see langword="false"/> the raw
    /// bytes are the final value (the tokenizer is not consuming character references).
    /// </summary>
    void Attribute(Utf8HtmlName name, ReadOnlySpan<Byte> value, Boolean valueMayContainReferences);

    void StartTagEnd(Boolean selfClosing);

    void EndTag(Utf8HtmlName name);

    void Comment(ReadOnlySpan<Byte> utf8) { }

    void ProcessingInstruction(ReadOnlySpan<Byte> utf8) => Comment(utf8);

    void Doctype(ReadOnlySpan<Byte> utf8) { }

    void Doctype(in Utf8DoctypeToken token) => Doctype(token.Name);

    Boolean WantsAttribute(Utf8HtmlName name);

    /// <summary>
    /// A 64-bit bloom filter over the semantic hashes of every attribute name the sink can still
    /// answer <see langword="true"/> for from <see cref="WantsAttribute"/> on the current start
    /// tag. The tokenizer reads it once per start tag, immediately after <see cref="StartTag"/>
    /// requests attribute capture, and rejects an attribute without materializing its name,
    /// calling <see cref="WantsAttribute"/>, or tracking it for duplicate suppression when
    /// <c>(filter &amp; Utf8NameHash.AttributeFilterBit(semanticHash)) == 0</c>. A cleared bit is
    /// therefore a promise; a set bit is only a hint (false positives fall through to
    /// <see cref="WantsAttribute"/>). Implementations that narrow the filter promise that their
    /// <see cref="WantsAttribute"/> answer is a pure function of the semantic (ASCII case-folded)
    /// name for the duration of the tag. The default wants everything, which preserves the exact
    /// unfiltered behavior.
    /// </summary>
    UInt64 StartTagAttributeFilter => UInt64.MaxValue;

    void EndOfFile() { }
}

/// <summary>
/// Opt-in capability for sinks that need the half-open normalized UTF-8 byte ranges of tags.
/// Each range callback immediately precedes the corresponding token callback.
/// </summary>
internal interface IUtf8HtmlStartTagSourceRangeSink
{
    Boolean WantsStartTagSourceRanges { get; }

    Boolean WantsEndTagSourceRanges => false;

    void StartTagSourceRange(Int64 sourceStart, Int64 sourceEnd);

    void EndTagSourceRange(Int64 sourceStart, Int64 sourceEnd) { }

    /// <summary>
    /// Reports each consumed normalized span after the tokenizer processed it, while the span is
    /// still addressable. <paramref name="publishableOffset"/> is the offset before which no future
    /// tag edit can land (see <c>Utf8HtmlTokenizer.RewritePublishableOffset</c>), so a
    /// streaming rewriter can publish everything below it straight from the borrowed span and only
    /// buffer the tail beyond it. Spans arrive contiguously in normalized-offset order.
    /// </summary>
    void ObserveNormalizedUtf8End(Int64 sourceStart, ReadOnlySpan<Byte> utf8, Int64 publishableOffset) { }
}

/// <summary>Opt-in delivery of borrowed, undecoded source text for streaming rewrites.</summary>
internal interface IUtf8HtmlRawTextSink
{
    bool IsRawTextEnabled { get; }

    bool WantsRawText { get; }

    void RawText(long sourceStart, ReadOnlySpan<byte> utf8, Utf8HtmlTextType textType, bool isLastInTextNode);
}

/// <summary>
/// Optional streaming comment capability. Implement this interface to consume comment payloads incrementally or to
/// decline them from <see cref="BeginComment"/> without materializing the complete payload in tokenizer scratch.
/// </summary>
internal interface IUtf8HtmlStreamingCommentSink
{
    /// <summary>Returns whether the payload of the next comment should be delivered.</summary>
    Boolean BeginComment();

    /// <summary>Consumes one complete, callback-scoped UTF-8 comment payload chunk.</summary>
    void CommentChunk(ReadOnlySpan<Byte> utf8);

    /// <summary>Completes the current comment.</summary>
    void EndComment();
}
