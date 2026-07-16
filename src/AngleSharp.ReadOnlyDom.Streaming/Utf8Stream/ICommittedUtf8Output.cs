namespace AngleSharp.ReadOnlyDom.Streaming.Utf8Stream;

/// <summary>
/// Exposes an irrevocable UTF-8 prefix produced by a query state. The memory remains valid until
/// <see cref="AdvanceCommitted"/> consumes it.
/// </summary>
public interface ICommittedUtf8Output
{
    ReadOnlyMemory<byte> CommittedUtf8 { get; }

    void AdvanceCommitted(int bytes);
}
