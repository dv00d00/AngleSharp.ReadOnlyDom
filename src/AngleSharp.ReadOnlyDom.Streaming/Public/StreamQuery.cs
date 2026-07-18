namespace AngleSharp.ReadOnlyDom.Streaming;

public static class StreamQuery
{
    public static QueryNode<TState> For<TState>(string rootTag) => QueryNode<TState>.Root(rootTag);

    /// <summary>
    /// Compiles independent query roots into one tokenizer pass, one structural stack, and one shared state value.
    /// </summary>
    public static QueryPlan<TState> Observe<TState>(params QueryNode<TState>[] queries) =>
        QueryPlanCompiler.CompileRoots(queries);
}
