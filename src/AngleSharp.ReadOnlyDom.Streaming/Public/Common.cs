namespace AngleSharp.ReadOnlyDom.Streaming;

internal enum QueryRelation : byte
{
    Root,
    Descendant,
    Child,
}

public enum QueryExecutionModel : byte
{
    LexicalStreaming,
}

public delegate void StartHandler<TState>(ref TState state, in Element element);

public delegate void TextHandler<TState>(ref TState state, ReadOnlySpan<byte> utf8);

public delegate void EndHandler<TState>(ref TState state);

public delegate void CompletedElementHandler<TState>(ref TState state, in CompletedElement element);

public delegate void RewriteHandler<TState>(
    ref TState state,
    in Element element,
    ref StartTagEditor startTag
);
