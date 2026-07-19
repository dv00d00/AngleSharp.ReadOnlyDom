#if NET10_0
using System.Buffers;
using System.Text;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

internal static class Utf8TokenSmoke
{
    public static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AngleSharp.ReadOnlyDom", "utf8-token-smoke");
        Directory.CreateDirectory(directory);
        var failures = 0;
        long totalBytes = 0;
        foreach (var document in BenchmarkCorpus.Load("full"))
        {
            var utf8 = Encoding.UTF8.GetBytes(document.Html);
            totalBytes += utf8.Length;
            var nativePath = Path.Combine(directory, document.Name + ".native.tokens.txt");
            var originalPath = Path.Combine(directory, document.Name + ".anglesharp.tokens.txt");
            WriteNative(utf8, nativePath, utf8.Length);
            WriteOriginal(document.Html, originalPath);
            var difference = FindFirstDifference(originalPath, nativePath);
            if (difference is null)
            {
                foreach (var segmentSize in new[] { 1, 7, 4096 })
                {
                    var segmentedPath = Path.Combine(directory, document.Name + $".native.{segmentSize}.tokens.txt");
                    WriteNative(utf8, segmentedPath, segmentSize);
                    difference = FindFirstDifference(originalPath, segmentedPath);
                    File.Delete(segmentedPath);
                    if (difference is not null)
                    {
                        difference = $"segment size {segmentSize}: {difference}";
                        break;
                    }
                }
            }
            if (difference is null)
                Console.WriteLine($"PASS {document.Name, -28} {utf8.Length, 12:N0} bytes");
            else
            {
                failures++;
                Console.WriteLine($"FAIL {document.Name}: {difference}");
            }
        }
        Console.WriteLine($"Checked 47 documents / {totalBytes:N0} UTF-8 bytes; failures: {failures}.");
        Console.WriteLine($"Traces: {directory}");
        return failures == 0 ? 0 : 1;
    }

    private static void WriteNative(byte[] utf8, string path, int segmentSize)
    {
        using var sink = new TraceSink(path);
        var tokenizer = new Utf8HtmlTokenizer(sink);
        for (var offset = 0; offset < utf8.Length; offset += segmentSize)
            tokenizer.Write(utf8.AsMemory(offset, Math.Min(segmentSize, utf8.Length - offset)));
        tokenizer.Complete();
    }

    private static void WriteOriginal(string html, string path)
    {
        using var sink = new TraceSink(path);
        using var tokenizer = new HtmlTokenizer(new TextSource(html), HtmlEntityProvider.ResolverExtended);
        while (true)
        {
            ref var token = ref tokenizer.GetStructToken();
            switch (token.Type)
            {
                case HtmlTokenType.Character:
                    sink.Text(Encoding.UTF8.GetBytes(token.Data.ToString()));
                    break;
                case HtmlTokenType.StartTag:
                    var name = token.Name.ToString();
                    sink.StartTag(Encoding.ASCII.GetBytes(name));
                    for (var index = 0; index < token.Attributes.Count; index++)
                    {
                        var attribute = token.Attributes[index];
                        sink.Attribute(
                            Encoding.ASCII.GetBytes(attribute.Name.ToString()),
                            Encoding.UTF8.GetBytes(attribute.Value.ToString())
                        );
                    }
                    sink.StartTagEnd(token.IsSelfClosing);
                    tokenizer.State = name switch
                    {
                        "title" or "textarea" => HtmlParseMode.RCData,
                        "style" or "xmp" or "iframe" or "noembed" or "noframes" => HtmlParseMode.Rawtext,
                        "script" => HtmlParseMode.Script,
                        "plaintext" => HtmlParseMode.Plaintext,
                        _ => tokenizer.State,
                    };
                    break;
                case HtmlTokenType.EndTag:
                    sink.EndTag(Encoding.ASCII.GetBytes(token.Name.ToString()));
                    break;
                case HtmlTokenType.Comment:
                    sink.Comment(Encoding.UTF8.GetBytes(token.Data.ToString()));
                    break;
                case HtmlTokenType.Doctype:
                    var doctype = new Utf8DoctypeToken(
                        Encoding.UTF8.GetBytes(token.Name.ToString()),
                        Encoding.UTF8.GetBytes(token.PublicIdentifier.ToString()),
                        token.IsPublicIdentifierMissing,
                        Encoding.UTF8.GetBytes(token.SystemIdentifier.ToString()),
                        token.IsSystemIdentifierMissing,
                        token.IsQuirksForced
                    );
                    sink.Doctype(in doctype);
                    break;
                case HtmlTokenType.EndOfFile:
                    sink.EndOfFile();
                    return;
            }
        }
    }

    private static string? FindFirstDifference(string expectedPath, string actualPath)
    {
        using var expected = new StreamReader(expectedPath);
        using var actual = new StreamReader(actualPath);
        for (var line = 1; ; line++)
        {
            var expectedLine = expected.ReadLine();
            var actualLine = actual.ReadLine();
            if (expectedLine == actualLine)
            {
                if (expectedLine is null)
                    return null;
                continue;
            }

            return $"first difference at normalized token {line}:{Environment.NewLine}"
                + $"  AngleSharp: {Preview(expectedLine)}{Environment.NewLine}"
                + $"  Native:     {Preview(actualLine)}";
        }
    }

    private static string Preview(string? value) => value is null ? "<EOF>" : value[..Math.Min(180, value.Length)];

    private sealed class TraceSink : IUtf8HtmlTokenSink, IDisposable
    {
        public Utf8HtmlTokenCapture Capture => Utf8HtmlTokenCapture.Text;

        private readonly StreamWriter _writer;
        private readonly ArrayBufferWriter<byte> _text = new(256);

        public TraceSink(string path) =>
            _writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        public void Text(ReadOnlySpan<byte> utf8)
        {
            var destination = _text.GetSpan(utf8.Length);
            utf8.CopyTo(destination);
            _text.Advance(utf8.Length);
        }

        public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name)
        {
            StartTag(name.Verbatim);
            return Utf8HtmlStartTagCapture.Attributes;
        }

        public void StartTag(ReadOnlySpan<byte> name)
        {
            FlushText();
            WriteSemantic("S", name);
        }

        public bool WantsAttribute(Utf8HtmlName name) => true;

        public void Attribute(Utf8HtmlName name, ReadOnlySpan<byte> value) => Attribute(name.Verbatim, value);

        public void Attribute(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {
            _writer.Write("A\t");
            _writer.Write(Convert.ToBase64String(GetSemanticBytes(name)));
            _writer.Write('\t');
            _writer.WriteLine(Convert.ToBase64String(value));
        }

        public void StartTagEnd(bool selfClosing) => _writer.WriteLine(selfClosing ? "G\t1" : "G\t0");

        public void EndTag(Utf8HtmlName name) => EndTag(name.Verbatim);

        public void EndTag(ReadOnlySpan<byte> name)
        {
            FlushText();
            WriteSemantic("E", name);
        }

        public void Comment(ReadOnlySpan<byte> utf8)
        {
            FlushText();
            Write("C", utf8);
        }

        public void Doctype(ReadOnlySpan<byte> utf8)
        {
            FlushText();
            Write("D", utf8);
        }

        public void Doctype(in Utf8DoctypeToken token)
        {
            FlushText();
            _writer.Write("D\t");
            _writer.Write(Convert.ToBase64String(token.Name));
            _writer.Write('\t');
            _writer.Write(token.IsPublicIdentifierMissing ? '1' : '0');
            _writer.Write('\t');
            _writer.Write(Convert.ToBase64String(token.PublicIdentifier));
            _writer.Write('\t');
            _writer.Write(token.IsSystemIdentifierMissing ? '1' : '0');
            _writer.Write('\t');
            _writer.Write(Convert.ToBase64String(token.SystemIdentifier));
            _writer.Write('\t');
            _writer.WriteLine(token.IsQuirksForced ? '1' : '0');
        }

        public void EndOfFile()
        {
            FlushText();
            _writer.WriteLine("F");
        }

        public void Dispose() => _writer.Dispose();

        private void FlushText()
        {
            if (_text.WrittenCount == 0)
                return;
            Write("T", _text.WrittenSpan);
            _text.Clear();
        }

        private void Write(string type, ReadOnlySpan<byte> value)
        {
            _writer.Write(type);
            _writer.Write('\t');
            _writer.WriteLine(Convert.ToBase64String(value));
        }

        private void WriteSemantic(string type, ReadOnlySpan<byte> name) => Write(type, GetSemanticBytes(name));

        private static byte[] GetSemanticBytes(ReadOnlySpan<byte> name)
        {
            var bytes = name.ToArray();
            for (var index = 0; index < bytes.Length; index++)
            {
                var value = bytes[index];
                if ((uint)(value - (byte)'A') <= 'Z' - 'A')
                    bytes[index] = (byte)(value | 0x20);
            }
            return bytes;
        }
    }
}
#endif
