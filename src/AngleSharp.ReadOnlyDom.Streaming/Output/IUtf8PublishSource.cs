namespace AngleSharp.ReadOnlyDom.Streaming.Output;

/// <summary>
/// Exposes UTF-8 bytes that are final and safe to publish downstream. The memory remains valid until
/// <see cref="AdvancePublished"/> consumes it after publication.
/// </summary>
public interface IUtf8PublishSource
{
    ReadOnlyMemory<byte> PublishableUtf8 { get; }

    void AdvancePublished(int bytes);
}
