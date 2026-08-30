using System.Buffers;
using System.Text.Json;
using AngleSharp.ReadOnlyDom.Streaming.Output;

namespace AngleSharp.ReadOnlyDom.HackerNews.Ndjson;

/// <summary>
/// Publishes one JSON object per line. A record is built in a scratch writer and only copied into the
/// publishable buffer once it is whole, so the frontier handed downstream never ends mid-object: a client
/// can parse and render every line it has received, whatever happens to the rest of the response.
/// </summary>
internal sealed class NdjsonPublisher : IUtf8PublishSource, IDisposable
{
    private readonly PublishableUtf8Buffer _output;
    private readonly ArrayBufferWriter<byte> _record = new(512);
    private readonly ArrayBufferWriter<byte>? _transcript;
    private readonly Utf8JsonWriter _json;

    internal NdjsonPublisher(bool recordTranscript = false, int initialCapacity = 8 * 1024)
    {
        _output = new PublishableUtf8Buffer(initialCapacity);
        _transcript = recordTranscript ? new ArrayBufferWriter<byte>(initialCapacity) : null;
        // Records are framed by newlines rather than by an array, so top-level validation is off by design.
        _json = new Utf8JsonWriter(_record, new JsonWriterOptions { SkipValidation = true });
    }

    /// <summary>The writer for the record under construction. Call <see cref="Commit"/> to publish it.</summary>
    internal Utf8JsonWriter Json => _json;

    /// <summary>Every published byte, retained only when the caller asked for a transcript.</summary>
    internal ReadOnlyMemory<byte> Transcript => _transcript?.WrittenMemory ?? ReadOnlyMemory<byte>.Empty;

    internal int RecordCount { get; private set; }

    public ReadOnlyMemory<byte> PublishableUtf8 => _output.PublishableUtf8;

    public void AdvancePublished(int bytes) => _output.AdvancePublished(bytes);

    public void Dispose() => _json.Dispose();

    internal void Commit()
    {
        _json.Flush();
        Copy(_record, "\n"u8);

        var line = _record.WrittenSpan;
        Copy(_output, line);
        if (_transcript is not null)
            Copy(_transcript, line);
        _output.MarkPublishable();

        _record.ResetWrittenCount();
        _json.Reset();
        RecordCount++;
    }

    internal static void Copy(IBufferWriter<byte> output, ReadOnlySpan<byte> value)
    {
        value.CopyTo(output.GetSpan(value.Length));
        output.Advance(value.Length);
    }
}
