using AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;

namespace AngleSharp.ReadOnlyDom.Streaming.Query;

internal enum QueryRelation : byte
{
    Root,
    Descendant,
    Child,
}

public delegate void StartHandler<TState>(ref TState state, in Element element);

public delegate void TextHandler<TState>(ref TState state, ReadOnlySpan<byte> utf8);

public delegate void EndHandler<TState>(ref TState state);

public delegate void CompletedElementHandler<TState>(ref TState state, in CompletedElement element);

public delegate void RewriteHandler<TState>(ref TState state, in Element element, ref ElementRewriter rewriter);

/// <summary>
/// Receives one output segment of a rewritten document. Segments borrow the caller's input or the
/// recorded edit payloads and are only valid for the duration of the call - copy them to keep them.
/// </summary>
public delegate void RewriteSegmentSink<TState>(ref TState state, ReadOnlySpan<byte> utf8);

/// <summary>
/// Receives one output segment of a streaming rewrite as soon as it can no longer change. Segments
/// borrow the session's input chunk, its holdback buffer, or recorded edit payloads and are only
/// valid for the duration of the call - copy them to keep them. Callers that need their own state
/// hold it in the enclosing scope; the session's Write loop is caller-owned.
/// </summary>
public delegate void StreamingRewriteSegmentSink(ReadOnlySpan<byte> utf8);
