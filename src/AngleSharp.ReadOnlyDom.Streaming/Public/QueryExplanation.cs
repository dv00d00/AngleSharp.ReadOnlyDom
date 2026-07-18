namespace AngleSharp.ReadOnlyDom.Streaming.Query;

public sealed record QueryExplanation
{
    internal QueryExplanation(
        QueryExecutionModel executionModel,
        IReadOnlyList<string> requiredTags,
        IReadOnlyList<string> requiredAttributes,
        int queryNodes,
        int estimatedFrameBytes
    )
    {
        ExecutionModel = executionModel;
        RequiredTags = Array.AsReadOnly(requiredTags.ToArray());
        RequiredAttributes = Array.AsReadOnly(requiredAttributes.ToArray());
        QueryNodes = queryNodes;
        EstimatedFrameBytes = estimatedFrameBytes;
    }

    public QueryExecutionModel ExecutionModel { get; }

    public IReadOnlyList<string> RequiredTags { get; }

    public IReadOnlyList<string> RequiredAttributes { get; }

    public int QueryNodes { get; }

    public int EstimatedFrameBytes { get; }
}
