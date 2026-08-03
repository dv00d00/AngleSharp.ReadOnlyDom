using System.Buffers;
using System.Buffers.Text;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using AngleSharp.ReadOnlyDom.Streaming;

internal static class StreamingContentJsonExample
{
    private static readonly byte[] Html =
        """
        <html>
          <head><title>Developer resources</title></head>
          <body>
            <main>
              <article data-kind="guide">
                <h2>Streaming HTML</h2>
                <p>Process large responses without retaining a DOM.</p>
                <a href="/guides/streaming">Read guide</a>
                <span class="reading-time" data-minutes="8">8 minutes</span>
              </article>
              <article data-kind="reference">
                <h2>Query API</h2>
                <p>Match known structures directly over UTF-8 input.</p>
                <a href="https://example.com/api">Open reference</a>
                <span class="reading-time" data-minutes="5">5 minutes</span>
              </article>
            </main>
          </body>
        </html>
        """u8.ToArray();

    private static readonly QueryPlan<ContentJsonOutput> Plan = CreatePlan();

    internal static async Task RunAsync()
    {
        Heading("STREAMING JSON — HTML resource cards become an NDJSON feed");

        await using var input = new MemoryStream(Html);
        await using var json = new MemoryStream();
        var reader = PipeReader.Create(input);
        var writer = PipeWriter.Create(json, new StreamPipeWriterOptions(leaveOpen: true));
        using var output = new ContentJsonOutput();
        try
        {
            await Plan.ExecuteBackpressuredAsync(reader, writer, output, flushThreshold: 1, inputSliceSize: 64);
        }
        finally
        {
            await reader.CompleteAsync();
            await writer.CompleteAsync();
        }

        Console.WriteLine(Encoding.UTF8.GetString(json.ToArray()));
        Console.WriteLine("transformation  : page metadata and resource fields are selected while HTML is parsed");
        Console.WriteLine(
            "output          : each complete NDJSON record is published through backpressured PipeWriter"
        );
        Console.WriteLine("materialization : no DOM or result list; only the current record is buffered");
    }

    private static QueryPlan<ContentJsonOutput> CreatePlan()
    {
        var html = StreamQuery.For<ContentJsonOutput>("html").OnEnd(static (ref output) => output.CompleteDocument());

        html.Descendant("head")
            .Child("title")
            .OnNormalizedText(static (ref output, in title) => output.WritePageTitle(title.TextUtf8));

        var resources = html.Descendant("body").Child("main");

        var resource = resources
            .Child("article")
            .Attribute("data-kind")
            .OnStart(static (ref output, in article) => output.StartResource(article), "data-kind")
            .OnEnd(static (ref output) => output.EndResource());

        resource
            .Child("h2")
            .OnNormalizedText(static (ref output, in heading) => output.WriteResourceTitle(heading.TextUtf8));
        
        resource.Child("p").OnNormalizedText(static (ref output, in summary) => output.WriteSummary(summary.TextUtf8));
        
        resource.Child("a").Attribute("href").OnClose(static (ref output, in link) => output.WriteUrl(link), "href");
        
        resource
            .Child("span")
            .Class("reading-time")
            .Attribute("data-minutes")
            .OnClose(static (ref output, in time) => output.WriteReadingMinutes(time), "data-minutes");

        return html.Compile();
    }

    private static void Heading(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));
    }

    private sealed class ContentJsonOutput : IUtf8PublishSource, IDisposable
    {
        private readonly PublishableUtf8Buffer _output = new();
        private readonly ArrayBufferWriter<byte> _record = new(512);
        private Utf8JsonWriter? _writer;
        private bool _completed;

        public ReadOnlyMemory<byte> PublishableUtf8 => _output.PublishableUtf8;

        public void AdvancePublished(int bytes) => _output.AdvancePublished(bytes);

        internal void WritePageTitle(ReadOnlySpan<byte> title)
        {
            StartRecord();
            Writer.WriteString("type"u8, "page"u8);
            Writer.WriteString("title"u8, title);
            EndRecord();
        }

        internal void StartResource(in Element article)
        {
            StartRecord();
            Writer.WriteString("type"u8, "resource"u8);
            if (article.TryGetAttribute("data-kind"u8, out var kind))
                Writer.WriteString("kind"u8, kind);
        }

        internal void WriteResourceTitle(ReadOnlySpan<byte> title) => Writer.WriteString("title"u8, title);

        internal void WriteSummary(ReadOnlySpan<byte> summary) => Writer.WriteString("summary"u8, summary);

        internal void WriteUrl(in CompletedElement link)
        {
            if (!link.TryGetAttributeUtf8("href"u8, out var href))
                return;
            Writer.WriteString("url"u8, href);
            Writer.WriteBoolean("external"u8, href.StartsWith("http://"u8) || href.StartsWith("https://"u8));
        }

        internal void WriteReadingMinutes(in CompletedElement time)
        {
            if (
                time.TryGetAttributeUtf8("data-minutes"u8, out var encoded)
                && Utf8Parser.TryParse(encoded, out int minutes, out var consumed)
                && consumed == encoded.Length
            )
                Writer.WriteNumber("readingMinutes"u8, minutes);
        }

        internal void EndResource() => EndRecord();

        internal void CompleteDocument()
        {
            if (_completed)
                return;
            if (_writer is not null)
                throw new InvalidOperationException("The HTML document ended inside a JSON record.");
            _completed = true;
        }

        private Utf8JsonWriter Writer =>
            _writer ?? throw new InvalidOperationException("No JSON record is currently open.");

        private void StartRecord()
        {
            if (_completed)
                throw new InvalidOperationException("The JSON output is already complete.");
            if (_writer is not null)
                throw new InvalidOperationException("Nested JSON records are not supported.");
            _record.Clear();
            _writer = new Utf8JsonWriter(_record);
            _writer.WriteStartObject();
        }

        private void EndRecord()
        {
            var writer = Writer;
            writer.WriteEndObject();
            writer.Flush();
            writer.Dispose();
            _writer = null;

            _output.Write(_record.WrittenSpan);
            _output.Write("\n"u8);
            _output.MarkPublishable();
        }

        public void Dispose() => _writer?.Dispose();
    }
}
