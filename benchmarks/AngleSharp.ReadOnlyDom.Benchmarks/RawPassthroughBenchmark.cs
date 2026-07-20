#if NET10_0

using System.Buffers;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

/// <summary>
/// Partitions the cost of the UTF-8 query rewrite path without hashing token payloads in the benchmark sink.
/// </summary>
[MemoryDiagnoser]
public class RawPassthroughBenchmark
{
    private static readonly QueryPlan<int> Html5MatchQuery = CreateHtml5Query(countMatches: true);
    private static readonly QueryPlan<int> Html5RewriteQuery = CreateHtml5Query(countMatches: false);
    private static readonly QueryPlan<int> QqMatchQuery = CreateQqQuery(countMatches: true);
    private static readonly QueryPlan<int> QqRewriteQuery = CreateQqQuery(countMatches: false);
    private static readonly QueryPlan<int> CompactNameMatchQuery = CreateCompactNameQuery(countMatches: true);
    private static readonly QueryPlan<int> CompactNameRewriteQuery = CreateCompactNameQuery(countMatches: false);
    private static readonly QueryPlan<int> FallbackNameMatchQuery = CreateFallbackNameQuery(countMatches: true);
    private static readonly QueryPlan<int> FallbackNameRewriteQuery = CreateFallbackNameQuery(countMatches: false);
    private static readonly QueryPlan<int> MixedNameMatchQuery = CreateMixedNameQuery(countMatches: true);
    private static readonly QueryPlan<int> MixedNameRewriteQuery = CreateMixedNameQuery(countMatches: false);

    private readonly MaterializingCommentSink _materializingCommentSink = new();
    private readonly DiscardingCommentSink _discardingCommentSink = new();
    private readonly RangeTrackingSink _rangeTrackingSink = new();
    private readonly TextCapturingSink _textCapturingSink = new();
    private byte[] _input = null!;
    private QueryPlan<int> _matchQuery = null!;
    private QueryPlan<int> _rewriteQuery = null!;

    [Params(
        "html5test-full",
        "html5test-no-comments",
        "html5test-no-scripts",
        "html5test-no-comments-or-scripts",
        "html5test-no-payload",
        "qq",
        "compact-names",
        "fallback-names",
        "mixed-name-duplicates"
    )]
    public string Page { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        var fullHtml5 = Page.StartsWith("html5test-", StringComparison.Ordinal)
            ? ReadRequiredFile("ANGLE_HTML5_FULL")
            : null;
        (_input, _matchQuery, _rewriteQuery) = Page switch
        {
            "html5test-full" => (fullHtml5!, Html5MatchQuery, Html5RewriteQuery),
            "html5test-no-comments" => (
                StripPayloads(fullHtml5!, stripComments: true, stripScripts: false),
                Html5MatchQuery,
                Html5RewriteQuery
            ),
            "html5test-no-scripts" => (
                StripPayloads(fullHtml5!, stripComments: false, stripScripts: true),
                Html5MatchQuery,
                Html5RewriteQuery
            ),
            "html5test-no-comments-or-scripts" => (
                StripPayloads(fullHtml5!, stripComments: true, stripScripts: true),
                Html5MatchQuery,
                Html5RewriteQuery
            ),
            "html5test-no-payload" => (ReadRequiredFile("ANGLE_HTML5_NOPAYLOAD"), Html5MatchQuery, Html5RewriteQuery),
            "qq" => (
                Encoding.UTF8.GetBytes(
                    BenchmarkCorpus.Load("full").Single(static document => document.Name == "qq").Html
                ),
                QqMatchQuery,
                QqRewriteQuery
            ),
            "compact-names" => (
                CreateNameFixture(
                    "<article id='item' class='card' href='/item' src='image' alt='preview' title='measured' "
                        + "name='entry' type='example' lang='en' width='320' height='200' rel='next' value='42' "
                        + "content='payload'>ordinary text</article>"
                ),
                CompactNameMatchQuery,
                CompactNameRewriteQuery
            ),
            "fallback-names" => (
                CreateNameFixture(
                    "<custom-element data-record='1' aria-label='item' http-equiv='refresh' accept-charset='utf-8' "
                        + "data-alpha='a' data-beta='b' data-gamma='c' data-delta='d' data-epsilon='e' "
                        + "data-zeta='f' data-eta='g' data-theta='h' data-iota='i' data-kappa='j' "
                        + "data-lambda='k' data-mu='l' data-nu='m' data-xi='n'>ordinary text</custom-element>"
                ),
                FallbackNameMatchQuery,
                FallbackNameRewriteQuery
            ),
            "mixed-name-duplicates" => (
                CreateNameFixture(
                    "<ArTiClE ID='first' id='ignored' CLASS='card' class='ignored' "
                        + "DaTa-Key='one' data-key='ignored' TITLE='title' title='ignored'>ordinary text</ArTiClE>"
                ),
                MixedNameMatchQuery,
                MixedNameRewriteQuery
            ),
            _ => throw new InvalidOperationException($"Unknown passthrough fixture '{Page}'."),
        };

        var matched = MatchOnly();
        var output = new ArrayBufferWriter<byte>(_input.Length + 1024);
        var rewritten = Rewrite(output);
        if (matched == 0 || matched != rewritten)
            throw new InvalidOperationException(
                $"The '{Page}' query produced {matched} direct matches and {rewritten} rewrite matches."
            );

        Console.WriteLine(
            $"{Page}: {_input.Length:N0} bytes, {matched:N0} matches, {output.WrittenCount:N0} output bytes."
        );
    }

    [Benchmark(Baseline = true)]
    public int DelimiterScan()
    {
        var remaining = _input.AsSpan();
        var checksum = 0;
        var consumed = 0;
        while (true)
        {
            var relative = remaining.IndexOf((byte)'<');
            if (relative < 0)
                return checksum + consumed;

            var width = relative + 1;
            consumed += width;
            checksum += consumed;
            remaining = remaining[width..];
        }
    }

    [Benchmark]
    public int CopyOnly()
    {
        var output = new ArrayBufferWriter<byte>(_input.Length);
        _input.CopyTo(output.GetSpan(_input.Length));
        output.Advance(_input.Length);
        return output.WrittenCount;
    }

    [Benchmark]
    public int TokenizeAndMaterializeComments()
    {
        _materializingCommentSink.Reset();
        Tokenize(_materializingCommentSink);
        return _materializingCommentSink.Checksum;
    }

    [Benchmark]
    public int TokenizeAndDiscardComments()
    {
        _discardingCommentSink.Reset();
        Tokenize(_discardingCommentSink);
        return _discardingCommentSink.Checksum;
    }

    [Benchmark]
    public int TokenizeDiscardingCommentsAndTrackRanges()
    {
        _rangeTrackingSink.Reset();
        Tokenize(_rangeTrackingSink);
        return _rangeTrackingSink.Checksum;
    }

    [Benchmark]
    public int TokenizeCapturingText()
    {
        _textCapturingSink.Reset();
        Tokenize(_textCapturingSink);
        return _textCapturingSink.Checksum;
    }

    [Benchmark]
    public int QueryMatchOnly() => MatchOnly();

    [Benchmark]
    public int QueryRewriteToNewBuffer()
    {
        var output = new ArrayBufferWriter<byte>(_input.Length + 1024);
        return Rewrite(output) ^ output.WrittenCount;
    }

    private void Tokenize(IUtf8HtmlTokenSink sink)
    {
        var tokenizer = new Utf8HtmlTokenizer(sink);
        tokenizer.Write(_input);
        tokenizer.Complete();
    }

    private int MatchOnly() => _matchQuery.Execute(_input, 0, Utf8InputContract.WellFormedUtf8);

    private int Rewrite(ArrayBufferWriter<byte> output) =>
        _rewriteQuery.Rewrite(
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

    private static QueryPlan<int> CreateHtml5Query(bool countMatches)
    {
        var match = StreamQuery.For<int>("a").Attribute("href");
        if (countMatches)
            match.OnStart(static (ref int count, in Element _) => count++);
        return match.Compile();
    }

    private static QueryPlan<int> CreateQqQuery(bool countMatches)
    {
        var list = StreamQuery.For<int>("ul").Class("news-list");
        var card = list.Descendant("li").Attribute("dt-eid", "em_item_article");
        var match = card.Descendant("a").Attribute("href");
        if (countMatches)
            match.OnStart(static (ref int count, in Element _) => count++);
        return list.Compile();
    }

    private static QueryPlan<int> CreateCompactNameQuery(bool countMatches)
    {
        var match = StreamQuery
            .For<int>("article")
            .Attribute("id", "item")
            .Attribute("class", "card")
            .Attribute("title", "measured")
            .Attribute("content", "payload");
        if (countMatches)
            match.OnStart(static (ref int count, in Element _) => count++);
        return match.Compile();
    }

    private static QueryPlan<int> CreateFallbackNameQuery(bool countMatches)
    {
        var match = StreamQuery
            .For<int>("custom-element")
            .Attribute("data-record", "1")
            .Attribute("aria-label", "item")
            .Attribute("http-equiv", "refresh");
        if (countMatches)
            match.OnStart(static (ref int count, in Element _) => count++);
        return match.Compile();
    }

    private static QueryPlan<int> CreateMixedNameQuery(bool countMatches)
    {
        var match = StreamQuery
            .For<int>("article")
            .Attribute("id", "first")
            .Attribute("class", "card")
            .Attribute("data-key", "one")
            .Attribute("title", "title");
        if (countMatches)
            match.OnStart(static (ref int count, in Element _) => count++);
        return match.Compile();
    }

    private static byte[] CreateNameFixture(string fragment)
    {
        const int targetBytes = 256 * 1024;
        var source = Encoding.UTF8.GetBytes(fragment);
        var copies = Math.Max(1, targetBytes / source.Length);
        var output = new byte[source.Length * copies];
        for (var offset = 0; offset < output.Length; offset += source.Length)
            source.CopyTo(output, offset);
        return output;
    }

    private static byte[] ReadRequiredFile(string variable)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException($"Environment variable {variable} must name the comparison fixture.");
        return File.ReadAllBytes(path);
    }

    private static byte[] StripPayloads(byte[] input, bool stripComments, bool stripScripts)
    {
        var output = new ArrayBufferWriter<byte>(input.Length);
        var remaining = input.AsSpan();
        while (!remaining.IsEmpty)
        {
            var commentStart = stripComments ? remaining.IndexOf("<!--"u8) : -1;
            var scriptStart = remaining.IndexOf("<script"u8);
            var nextStart = FirstNonNegative(commentStart, scriptStart);
            if (nextStart < 0)
            {
                Write(output, remaining);
                break;
            }

            Write(output, remaining[..nextStart]);
            remaining = remaining[nextStart..];
            if (scriptStart == nextStart)
            {
                var openingEnd = remaining.IndexOf((byte)'>');
                if (openingEnd < 0)
                    throw new InvalidOperationException("The benchmark fixture contains an unterminated script tag.");

                openingEnd++;
                var closingStart = remaining[openingEnd..].IndexOf("</script"u8);
                if (closingStart < 0)
                    throw new InvalidOperationException("The benchmark fixture contains an unterminated script body.");

                closingStart += openingEnd;
                var closingEnd = remaining[closingStart..].IndexOf((byte)'>');
                if (closingEnd < 0)
                    throw new InvalidOperationException(
                        "The benchmark fixture contains an unterminated script end tag."
                    );

                closingEnd += closingStart + 1;
                if (stripScripts)
                {
                    Write(output, remaining[..openingEnd]);
                    Write(output, remaining[closingStart..closingEnd]);
                }
                else
                {
                    Write(output, remaining[..closingEnd]);
                }

                remaining = remaining[closingEnd..];
                continue;
            }

            var commentEnd = remaining.IndexOf("-->"u8);
            if (commentEnd < 0)
                throw new InvalidOperationException("The benchmark fixture contains an unterminated comment.");

            Write(output, "<!---->"u8);
            remaining = remaining[(commentEnd + 3)..];
        }

        return output.WrittenSpan.ToArray();
    }

    private static int FirstNonNegative(int left, int right) =>
        left < 0 ? right
        : right < 0 ? left
        : Math.Min(left, right);

    private static void Write(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> value)
    {
        value.CopyTo(output.GetSpan(value.Length));
        output.Advance(value.Length);
    }

    private sealed class MaterializingCommentSink : IUtf8HtmlTokenSink
    {
        public Utf8HtmlTokenCapture Capture => Utf8HtmlTokenCapture.None;
        public int Checksum { get; private set; }

        public void Reset() => Checksum = 0;

        public void Text(ReadOnlySpan<byte> utf8) => throw new InvalidOperationException();

        public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name)
        {
            Checksum++;
            return Utf8HtmlStartTagCapture.None;
        }

        public void Attribute(Utf8HtmlName name, ReadOnlySpan<byte> value) => throw new InvalidOperationException();

        public void StartTagEnd(bool selfClosing) => Checksum += selfClosing ? 2 : 1;

        public void EndTag(Utf8HtmlName name) => Checksum++;

        public void Comment(ReadOnlySpan<byte> utf8) => Checksum += utf8.Length;

        public bool WantsAttribute(Utf8HtmlName name) => false;
    }

    private sealed class DiscardingCommentSink : IUtf8HtmlTokenSink, IUtf8HtmlStreamingCommentSink
    {
        public Utf8HtmlTokenCapture Capture => Utf8HtmlTokenCapture.None;
        public int Checksum { get; private set; }

        public void Reset() => Checksum = 0;

        public void Text(ReadOnlySpan<byte> utf8) => throw new InvalidOperationException();

        public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name)
        {
            Checksum++;
            return Utf8HtmlStartTagCapture.None;
        }

        public void Attribute(Utf8HtmlName name, ReadOnlySpan<byte> value) => throw new InvalidOperationException();

        public void StartTagEnd(bool selfClosing) => Checksum += selfClosing ? 2 : 1;

        public void EndTag(Utf8HtmlName name) => Checksum++;

        public bool WantsAttribute(Utf8HtmlName name) => false;

        public bool BeginComment() => false;

        public void CommentChunk(ReadOnlySpan<byte> utf8) => throw new InvalidOperationException();

        public void EndComment() { }
    }

    private sealed class RangeTrackingSink
        : IUtf8HtmlTokenSink,
            IUtf8HtmlStreamingCommentSink,
            IUtf8HtmlStartTagSourceRangeSink
    {
        public Utf8HtmlTokenCapture Capture => Utf8HtmlTokenCapture.None;
        public bool WantsStartTagSourceRanges => true;
        public int Checksum { get; private set; }

        public void Reset() => Checksum = 0;

        public void Text(ReadOnlySpan<byte> utf8) => throw new InvalidOperationException();

        public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name)
        {
            Checksum++;
            return Utf8HtmlStartTagCapture.None;
        }

        public void Attribute(Utf8HtmlName name, ReadOnlySpan<byte> value) => throw new InvalidOperationException();

        public void StartTagEnd(bool selfClosing) => Checksum += selfClosing ? 2 : 1;

        public void StartTagSourceRange(long sourceStart, long sourceEnd) =>
            Checksum += unchecked((int)(sourceEnd - sourceStart));

        public void EndTag(Utf8HtmlName name) => Checksum++;

        public bool WantsAttribute(Utf8HtmlName name) => false;

        public bool BeginComment() => false;

        public void CommentChunk(ReadOnlySpan<byte> utf8) => throw new InvalidOperationException();

        public void EndComment() { }
    }

    private sealed class TextCapturingSink : IUtf8HtmlTokenSink, IUtf8HtmlStreamingCommentSink
    {
        public Utf8HtmlTokenCapture Capture => Utf8HtmlTokenCapture.Text;
        public int Checksum { get; private set; }

        public void Reset() => Checksum = 0;

        public void Text(ReadOnlySpan<byte> utf8) => Checksum += utf8.Length;

        public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name)
        {
            Checksum++;
            return Utf8HtmlStartTagCapture.None;
        }

        public void Attribute(Utf8HtmlName name, ReadOnlySpan<byte> value) => throw new InvalidOperationException();

        public void StartTagEnd(bool selfClosing) => Checksum += selfClosing ? 2 : 1;

        public void EndTag(Utf8HtmlName name) => Checksum++;

        public bool WantsAttribute(Utf8HtmlName name) => false;

        public bool BeginComment() => false;

        public void CommentChunk(ReadOnlySpan<byte> utf8) => throw new InvalidOperationException();

        public void EndComment() { }
    }
}

#endif
