namespace AngleSharp.ReadOnlyDom.HackerNews.Upstream;

/// <summary>
/// A read-only pass-through that counts what was actually pulled from an upstream response and can be cut
/// short: once <see cref="StopReading"/> is called it reports end of input instead of reading further.
/// <para>
/// Stopping this way rather than by cancelling the execution matters. The publishing loop copies a
/// publishable prefix into the response, flushes, and only then marks it consumed — cancel between those
/// steps and the prefix is on the wire but still looks unpublished. Ending the input instead lets the
/// tokenizer finish normally on end-of-input, so every record is published exactly once.
/// </para>
/// </summary>
internal sealed class EarlyStopStream(Stream inner) : Stream
{
    private bool _stopped;

    internal long BytesRead { get; private set; }

    internal bool Stopped => _stopped;

    internal void StopReading() => _stopped = true;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_stopped)
            return 0;

        var read = inner.Read(buffer);
        BytesRead += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_stopped)
            return 0;

        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        BytesRead += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
