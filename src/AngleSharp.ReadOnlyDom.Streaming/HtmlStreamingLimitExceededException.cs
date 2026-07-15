namespace AngleSharp.ReadOnlyDom.Streaming;

public enum HtmlStreamingLimit
{
    BufferedTokenBytes,
    NestingDepth,
    InputBytes,
    QueryCaptureBytes,
}

/// <summary>Thrown before a streaming HTML execution exceeds a configured resource limit.</summary>
public sealed class HtmlStreamingLimitExceededException : IOException
{
    public HtmlStreamingLimitExceededException(HtmlStreamingLimit limit, long allowed, long observed)
        : base($"Streaming HTML {limit} limit exceeded: observed {observed:N0}, allowed {allowed:N0}.")
    {
        Limit = limit;
        Allowed = allowed;
        Observed = observed;
    }

    public HtmlStreamingLimit Limit { get; }

    public long Allowed { get; }

    public long Observed { get; }
}
