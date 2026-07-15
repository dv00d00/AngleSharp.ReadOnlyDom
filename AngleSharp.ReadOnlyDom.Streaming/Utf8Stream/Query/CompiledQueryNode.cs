namespace AngleSharp.ReadOnlyDom.Streaming.Utf8Stream.Query;

internal sealed record CompiledQueryNode<TState>(
    int Index,
    int ParentIndex,
    QueryRelation Relation,
    byte[] TagName,
    ulong TagHash,
    ulong RequiredAttributeBits,
    CompiledAttributePredicate[] Predicates,
    StartHandler<TState>? Start,
    TextHandler<TState>? Text,
    EndHandler<TState>? End,
    CompletedElementHandler<TState>? Completed,
    CompletedTextMode CompletedTextMode,
    int[] CompletedAttributeIndexes
);