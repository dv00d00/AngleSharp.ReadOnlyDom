namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

#pragma warning disable CS1591 // Experimental diagnostics surface; not proposed as final API.

internal readonly record struct Utf8HtmlTokenizerCounters(
    Int64 BytesConsumed,
    Int64 InputSegments,
    Int64 Reconsumes,
    Int32 MaximumBufferedTokenBytes
);
