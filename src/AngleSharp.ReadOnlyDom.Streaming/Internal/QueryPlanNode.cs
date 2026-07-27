namespace AngleSharp.ReadOnlyDom.Streaming;

internal sealed record QueryPlanNode<TState>(
    int Index,
    int ParentIndex,
    QueryRelation Relation,
    byte[] TagNameUtf8,
    ulong TagIdentity,
    int TagIdentityLength,
    ulong RequestedAttributeMask,
    CompiledAttributePredicate[] Predicates,
    StartHandler<TState>? Start,
    TextHandler<TState>? Text,
    EndHandler<TState>? End,
    CompletedElementHandler<TState>? Completed,
    CompletedTextMode CompletedTextMode,
    int[] CapturedAttributeIndexes
);
