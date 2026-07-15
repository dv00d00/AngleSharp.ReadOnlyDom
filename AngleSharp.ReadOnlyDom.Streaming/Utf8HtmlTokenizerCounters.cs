namespace AngleSharp.ReadOnlyDom.Streaming;

public readonly record struct Utf8HtmlTokenizerCounters(
    long BytesConsumed,
    long InputSegments,
    long Reconsumes,
    int MaximumSourceLookbehind,
    int MaximumBufferedTokenBytes
);