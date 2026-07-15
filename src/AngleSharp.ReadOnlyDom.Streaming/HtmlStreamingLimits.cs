namespace AngleSharp.ReadOnlyDom.Streaming;

/// <summary>Bounds resources retained or consumed by a streaming HTML execution.</summary>
public sealed class HtmlStreamingLimits
{
    public const int DefaultMaximumBufferedTokenBytes = 1024 * 1024;
    public const int DefaultMaximumNestingDepth = 4096;
    public const long DefaultMaximumInputBytes = 128L * 1024 * 1024;
    public const long DefaultMaximumQueryCaptureBytes = 64L * 1024 * 1024;

    public static HtmlStreamingLimits Default { get; } = new();

    public static HtmlStreamingLimits Unlimited { get; } =
        new(int.MaxValue, int.MaxValue, long.MaxValue, long.MaxValue);

    public HtmlStreamingLimits(
        int maximumBufferedTokenBytes = DefaultMaximumBufferedTokenBytes,
        int maximumNestingDepth = DefaultMaximumNestingDepth,
        long maximumInputBytes = DefaultMaximumInputBytes,
        long maximumQueryCaptureBytes = DefaultMaximumQueryCaptureBytes
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBufferedTokenBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumNestingDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumInputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumQueryCaptureBytes);
        MaximumBufferedTokenBytes = maximumBufferedTokenBytes;
        MaximumNestingDepth = maximumNestingDepth;
        MaximumInputBytes = maximumInputBytes;
        MaximumQueryCaptureBytes = maximumQueryCaptureBytes;
    }

    public int MaximumBufferedTokenBytes { get; }

    public int MaximumNestingDepth { get; }

    public long MaximumInputBytes { get; }

    public long MaximumQueryCaptureBytes { get; }
}
