#if NET10_0

using System.Buffers;
using System.Text;
using System.Text.Unicode;
using AngleSharp.Html.Parser.Utf8;
using AngleSharp.ReadOnlyDom.Streaming;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[MemoryDiagnoser]
public class QueryHeadroomBenchmark
{
    private static readonly QueryPlan<int> ObservedQuery = CreateQuery(observeMatches: true);
    private static readonly QueryPlan<int> RewriteQuery = CreateQuery(observeMatches: false);
    private readonly StructureSink _structureSink = new();
    private byte[] _input = null!;

    [GlobalSetup]
    public void Setup()
    {
        _input = Encoding.UTF8.GetBytes(
            BenchmarkCorpus.Load("full").Single(static document => document.Name == "qq").Html
        );
        if (!Utf8.IsValid(_input))
            throw new InvalidOperationException("The QQ fixture must be well-formed UTF-8.");

        var observed = ObservedQuery.Execute(_input, 0);
        if (observed != 16)
            throw new InvalidOperationException($"Expected 16 observed links, found {observed}.");

        var unchanged = new ArrayBufferWriter<byte>(_input.Length + 1024);
        var unchangedMatches = RewriteWithoutEdits(unchanged, Utf8InputContract.WellFormedUtf8);
        if (unchangedMatches != observed || !unchanged.WrittenSpan.SequenceEqual(_input))
            throw new InvalidOperationException("No-edit rewriting did not reproduce the source exactly.");

        var edited = new ArrayBufferWriter<byte>(_input.Length + 1024);
        var editedMatches = RewriteWithEdits(edited);
        if (editedMatches != observed || edited.WrittenCount <= _input.Length)
            throw new InvalidOperationException("Edited rewriting did not produce the expected matches and growth.");

        Console.WriteLine(
            $"qq headroom: {_input.Length:N0} input bytes, {observed} matches, "
                + $"{edited.WrittenCount:N0} edited bytes."
        );
    }

    [Benchmark(Baseline = true)]
    public int CopyToNewBuffer()
    {
        var output = new ArrayBufferWriter<byte>(_input.Length + 1024);
        output.Write(_input);
        return output.WrittenCount;
    }

    [Benchmark]
    public bool ValidateOnly() => Utf8.IsValid(_input);

    [Benchmark]
    public int StructureTokenizerTrusted()
    {
        _structureSink.Reset();
        var tokenizer = new Utf8HtmlTokenizer(_structureSink);
        tokenizer.Write(_input);
        tokenizer.Complete();
        return _structureSink.Checksum;
    }

    [Benchmark]
    public int StructureTokenizerValidated()
    {
        _structureSink.Reset();
        var tokenizer = new Utf8HtmlTokenizer(_structureSink);
        var input = new Utf8HtmlTokenizerInput(tokenizer);
        input.Write(_input);
        input.Complete();
        return _structureSink.Checksum;
    }

    [Benchmark]
    public int QueryExecuteTrusted() => ObservedQuery.Execute(_input, 0);

    [Benchmark]
    public int QueryExecuteValidated()
    {
        using var execution = ObservedQuery.CreateExecution(0);
        var tokenizer = new Utf8HtmlTokenizer(execution);
        var input = new Utf8HtmlTokenizerInput(tokenizer);
        input.Write(_input);
        input.Complete();
        return execution.State;
    }

    [Benchmark]
    public int RewriteNoEditsValidated()
    {
        var output = new ArrayBufferWriter<byte>(_input.Length + 1024);
        return RewriteWithoutEdits(output, Utf8InputContract.ArbitraryBytes) ^ output.WrittenCount;
    }

    [Benchmark]
    public int RewriteNoEditsTrusted()
    {
        var output = new ArrayBufferWriter<byte>(_input.Length + 1024);
        return RewriteWithoutEdits(output, Utf8InputContract.WellFormedUtf8) ^ output.WrittenCount;
    }

    [Benchmark]
    public int RewriteSixteenEditsTrusted()
    {
        var output = new ArrayBufferWriter<byte>(_input.Length + 1024);
        return RewriteWithEdits(output) ^ output.WrittenCount;
    }

    private int RewriteWithoutEdits(ArrayBufferWriter<byte> output, Utf8InputContract inputContract) =>
        RewriteQuery.Rewrite(
            _input,
            output,
            0,
            static (ref int count, in Element _, ref StartTagEditor _) => count++,
            inputContract
        );

    private int RewriteWithEdits(ArrayBufferWriter<byte> output) =>
        RewriteQuery.Rewrite(
            _input,
            output,
            0,
            static (ref int count, in Element _, ref StartTagEditor tag) =>
            {
                count++;
                tag.AppendAttribute("data-query-hit"u8, "1"u8);
            },
            Utf8InputContract.WellFormedUtf8
        );

    private static QueryPlan<int> CreateQuery(bool observeMatches)
    {
        var list = StreamQuery.For<int>("ul").Class("news-list");
        var card = list.Descendant("li").Attribute("dt-eid", "em_item_article");
        var link = card.Descendant("a").Attribute("href");
        if (observeMatches)
            link.OnStart(static (ref int count, in Element _) => count++);
        return list.Compile();
    }

    private sealed class StructureSink : IUtf8HtmlTokenSink
    {
        public Utf8HtmlTokenCapture Capture => Utf8HtmlTokenCapture.None;

        public int Checksum { get; private set; }

        public void Reset() => Checksum = 17;

        public void Text(ReadOnlySpan<byte> utf8) =>
            throw new InvalidOperationException("The structure-only tokenizer emitted text.");

        public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name)
        {
            Checksum = unchecked((Checksum * 31) ^ (int)name.SemanticHash);
            return Utf8HtmlStartTagCapture.None;
        }

        public bool WantsAttribute(Utf8HtmlName name) => false;

        public void Attribute(Utf8HtmlName name, ReadOnlySpan<byte> value) =>
            throw new InvalidOperationException("The structure-only tokenizer emitted an attribute.");

        public void StartTagEnd(bool selfClosing) =>
            Checksum = unchecked((Checksum * 31) ^ (selfClosing ? 1 : 0));

        public void EndTag(Utf8HtmlName name) =>
            Checksum = unchecked((Checksum * 31) ^ (int)name.SemanticHash);

        public void EndOfFile() => Checksum = unchecked((Checksum * 31) ^ -1);
    }
}

#endif
