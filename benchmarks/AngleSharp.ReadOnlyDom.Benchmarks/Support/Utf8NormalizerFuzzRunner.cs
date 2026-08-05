#if NET10_0
using System.Text;
using AngleSharp.ReadOnlyDom.Benchmarks.Suites.Utf8;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

namespace AngleSharp.ReadOnlyDom.Benchmarks.Support;

// Property fuzz for the input normalizer and the unvalidated-input tokenizer paths. Per random
// HTML document (seeded, half salted with malformed UTF-8):
//   1. Arbitrary-contract output is invariant to input chunking (1..4096-byte chunks vs whole).
//   2. Arbitrary-contract output equals an independent oracle: the document normalized by
//      System.Text.Encoding.UTF8 (the reference maximal-subpart decoder, sharing no code with
//      the normalizer) and fed through the trusted contract.
// Both across three sink modes: text-capturing, attributes-only, and discard-everything - the
// last one returns no captures at all, which is what routes tag tails through the raw
// ScanDiscardedTagTail path that skips validation entirely. Chunking invariance is the property
// that catches replacement-ordering bugs: a U+FFFD emitted from a carry drains at the wrong
// stream position long before any unit test notices.
//
//   dotnet AngleSharp.ReadOnlyDom.Benchmarks.dll --utf8-normalizer-fuzz [iterations] [seed]
internal static class Utf8NormalizerFuzzRunner
{
    private static readonly int[] ChunkSizes = [1, 2, 3, 7, 13, 64, 127, 129, 1000, 4096];

    private enum SinkMode
    {
        CaptureTextAndAttributes,
        CaptureAttributesOnly,
        DiscardEverything,
    }

    public static int Run(string[] args)
    {
        var iterations = args.Length > 0 ? int.Parse(args[0]) : 500;
        var seed = args.Length > 1 ? int.Parse(args[1]) : 12345;
        var random = new Random(seed);
        var failures = 0;

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var onlyValidUtf8 = iteration % 2 == 0;
            var document = BuildDocument(random, onlyValidUtf8);

            // Independent oracle: .NET's reference decoder performs the same maximal-subpart
            // U+FFFD replacement the arbitrary contract promises, so the normalized bytes fed
            // through the trusted contract must produce the identical token stream.
            var normalized = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(document));

            foreach (var mode in new[] { SinkMode.CaptureTextAndAttributes, SinkMode.CaptureAttributesOnly, SinkMode.DiscardEverything })
            {
                var reference = Fingerprint(document, Utf8InputContract.ArbitraryBytes, mode, [document.Length]);

                var oracle = Fingerprint(normalized, Utf8InputContract.WellFormedUtf8, mode, [normalized.Length]);
                if (oracle != reference)
                {
                    failures++;
                    Console.WriteLine($"ORACLE MISMATCH iter={iteration} mode={mode} bytes={document.Length}");
                    DumpRepro(document, iteration);
                }

                foreach (var chunkSize in ChunkSizes)
                {
                    var chunked = Fingerprint(document, Utf8InputContract.ArbitraryBytes, mode, Chunks(document.Length, chunkSize, random));
                    if (chunked != reference)
                    {
                        failures++;
                        Console.WriteLine($"CHUNKING MISMATCH iter={iteration} mode={mode} chunk={chunkSize} bytes={document.Length}");
                        DumpRepro(document, iteration);
                        break;
                    }
                }
            }
        }

        Console.WriteLine(failures == 0 ? $"OK: {iterations} documents, no divergence." : $"FAILED: {failures} divergences.");
        return failures == 0 ? 0 : 1;
    }

    private static IEnumerable<int> Chunks(int total, int chunkSize, Random random)
    {
        var remaining = total;
        while (remaining > 0)
        {
            // Jitter every third chunk so window and carry boundaries move around.
            var size = Math.Min(remaining, random.Next(3) == 0 ? random.Next(1, chunkSize + 1) : chunkSize);
            yield return size;
            remaining -= size;
        }
    }

    private static ulong Fingerprint(byte[] document, Utf8InputContract contract, SinkMode mode, IEnumerable<int> chunkSizes)
    {
        var sink = new FuzzSink(mode);
        var tokenizer = new Utf8HtmlTokenizer(sink);
        var input = new Utf8HtmlTokenizerInput(tokenizer, contract);
        var offset = 0;
        foreach (var size in chunkSizes)
        {
            input.Write(document.AsMemory(offset, size));
            offset += size;
        }
        if (offset != document.Length)
        {
            throw new InvalidOperationException("chunker bug");
        }
        input.Complete();
        return sink.Inner.Fingerprint;
    }

    private static byte[] BuildDocument(Random random, bool onlyValidUtf8)
    {
        var builder = new List<byte>();
        var parts = random.Next(4, 60);
        for (var part = 0; part < parts; part++)
        {
            switch (random.Next(13))
            {
                case 0:
                    builder.AddRange(Encoding.UTF8.GetBytes($"<div class=\"a{random.Next(100)}\" data-x='v{random.Next(100)}'>"));
                    break;
                case 1:
                    builder.AddRange(Encoding.UTF8.GetBytes("</div>"));
                    break;
                case 2:
                    AddText(builder, random, onlyValidUtf8, random.Next(1, 400));
                    break;
                case 3:
                    builder.AddRange(Encoding.UTF8.GetBytes("<script>var a = 1 < 2; // "));
                    AddText(builder, random, onlyValidUtf8, random.Next(1, 600));
                    builder.AddRange(Encoding.UTF8.GetBytes("</script>"));
                    break;
                case 4:
                    builder.AddRange(Encoding.UTF8.GetBytes("<style>.x{content:\""));
                    AddText(builder, random, onlyValidUtf8, random.Next(1, 300));
                    builder.AddRange(Encoding.UTF8.GetBytes("\"}</style>"));
                    break;
                case 5:
                    builder.AddRange(Encoding.UTF8.GetBytes($"<!-- comment {random.Next(1000)} -->"));
                    break;
                case 6:
                    builder.AddRange(Encoding.UTF8.GetBytes("&amp; &lt; &#x4E2D; &unknown;"));
                    break;
                case 7:
                    builder.AddRange(Encoding.UTF8.GetBytes("<title>rc "));
                    AddText(builder, random, onlyValidUtf8, random.Next(1, 200));
                    builder.AddRange(Encoding.UTF8.GetBytes("&amp;</title>"));
                    break;
                case 8:
                    builder.Add((byte)'\r');
                    if (random.Next(2) == 0)
                        builder.Add((byte)'\n');
                    break;
                case 9:
                    builder.Add(0);
                    break;
                case 10:
                    builder.AddRange(Encoding.UTF8.GetBytes($"<textarea>t{random.Next(10)} "));
                    AddText(builder, random, onlyValidUtf8, random.Next(1, 200));
                    builder.AddRange(Encoding.UTF8.GetBytes("</textarea>"));
                    break;
                case 11:
                    // Arbitrary bytes inside a tag tail: quoted and unquoted attribute values and
                    // an attribute name. With a discarding sink these travel the raw scan paths.
                    builder.AddRange(Encoding.UTF8.GetBytes("<span junk=\""));
                    AddText(builder, random, onlyValidUtf8, random.Next(1, 60));
                    builder.AddRange(Encoding.UTF8.GetBytes("' x"));
                    AddText(builder, random, onlyValidUtf8, random.Next(1, 8));
                    builder.AddRange(Encoding.UTF8.GetBytes("\" u="));
                    AddText(builder, random, onlyValidUtf8, random.Next(1, 30));
                    builder.AddRange(Encoding.UTF8.GetBytes(random.Next(2) == 0 ? ">" : " q='"));
                    if (builder[^1] == (byte)'\'')
                    {
                        AddText(builder, random, onlyValidUtf8, random.Next(1, 30));
                        builder.AddRange(Encoding.UTF8.GetBytes("'>"));
                    }
                    break;
                default:
                    AddText(builder, random, onlyValidUtf8, random.Next(1, 50));
                    break;
            }
        }
        if (random.Next(4) == 0 && !onlyValidUtf8)
        {
            // Dangling tail at end of stream: incomplete (E4 / E4 B8) or already malformed (E0 87).
            builder.Add(random.Next(3) == 0 ? (byte)0xE0 : (byte)0xE4);
            if (random.Next(2) == 0)
                builder.Add(random.Next(3) == 0 ? (byte)0x87 : (byte)0xB8);
        }
        return [.. builder];
    }

    private static void AddText(List<byte> builder, Random random, bool onlyValidUtf8, int length)
    {
        for (var index = 0; index < length; index++)
        {
            switch (random.Next(onlyValidUtf8 ? 4 : 6))
            {
                case 0:
                case 1:
                    builder.Add((byte)random.Next(0x20, 0x7F));
                    break;
                case 2:
                    builder.AddRange("中"u8);
                    break;
                case 3:
                    builder.AddRange(random.Next(3) switch
                    {
                        0 => "é"u8.ToArray(),
                        1 => "😀"u8.ToArray(),
                        _ => " край"u8.ToArray(),
                    });
                    break;
                case 4:
                    // Malformed: lone continuation, bare lead, or an invalid byte.
                    builder.Add(random.Next(3) switch
                    {
                        0 => (byte)random.Next(0x80, 0xC0),
                        1 => (byte)random.Next(0xC2, 0xF5),
                        _ => (byte)0xFF,
                    });
                    break;
                default:
                    builder.Add(0xE4);
                    builder.Add(0xB8);
                    builder.Add((byte)'x');
                    break;
            }
        }
    }

    private static void DumpRepro(byte[] document, int iteration)
    {
        var path = Path.Combine(Path.GetTempPath(), $"normalizer-fuzz-{iteration}.bin");
        File.WriteAllBytes(path, document);
        Console.WriteLine($"  repro: {path}");
    }

    private sealed class FuzzSink(Utf8NormalizerFuzzRunner.SinkMode mode) : IUtf8HtmlTokenSink
    {
        public Utf8TokenizerBaselineBenchmark.FingerprintSink Inner { get; } = new();

        public Utf8HtmlTokenCapture Capture =>
            mode == SinkMode.CaptureTextAndAttributes ? Utf8HtmlTokenCapture.Text : Utf8HtmlTokenCapture.None;

        public void Text(ReadOnlySpan<byte> utf8) => Inner.Text(utf8);

        public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name)
        {
            var capture = Inner.StartTag(name);
            return mode == SinkMode.DiscardEverything ? Utf8HtmlStartTagCapture.None : capture;
        }

        public bool WantsAttribute(Utf8HtmlName name) =>
            mode != SinkMode.DiscardEverything && Inner.WantsAttribute(name);

        public void Attribute(Utf8HtmlName name, ReadOnlySpan<byte> value) => Inner.Attribute(name, value);

        public void StartTagEnd(bool selfClosing) => Inner.StartTagEnd(selfClosing);

        public void EndTag(Utf8HtmlName name) => Inner.EndTag(name);
    }
}
#endif
