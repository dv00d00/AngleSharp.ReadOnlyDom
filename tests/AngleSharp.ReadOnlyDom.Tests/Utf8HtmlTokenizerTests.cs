#if NET10_0
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens;
using AngleSharp.Text;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.ReadOnlyDom.Streaming.Input;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

namespace AngleSharp.Readonly.Tests;

public sealed class Utf8HtmlTokenizerTests
{
    private const string Html =
        "<!doctype html><main data-id='42' disabled>hé &amp; <b title=x>bold</b><style>a>b{color:red}</style><!--tail--></main>";

    [Test]
    public async Task EveryByteBoundaryMatchesContiguousUtf8Input()
    {
        var utf8 = Encoding.UTF8.GetBytes(Html);
        var contiguous = Tokenize(utf8, utf8.Length);
        var bytewise = Tokenize(utf8, 1);

        await Assert.That(bytewise.Events).IsEquivalentTo(contiguous.Events);
        await Assert.That(bytewise.Counters.BytesConsumed).IsEqualTo(utf8.Length);
        await Assert.That(bytewise.Counters.InputSegments).IsEqualTo(utf8.Length);
    }

    [Test]
    [Arguments("<a first=1 second=2>tail", false)]
    [Arguments("<a first='1' second=2>tail", false)]
    [Arguments("<a first=1 second=2>tail", true)]
    public async Task YieldRequestedByAttributeCallbackStopsTheCurrentWrite(String html, Boolean requestFromWants)
    {
        var sink = new YieldingAttributeSink(requestFromWants);
        var tokenizer = new Utf8HtmlTokenizer(sink);
        sink.Tokenizer = tokenizer;
        var utf8 = Encoding.UTF8.GetBytes(html);

        var consumed = tokenizer.WriteUntilYield(utf8);

        await Assert.That(consumed).IsLessThan(utf8.Length);
        await Assert.That(sink.Attributes.Count).IsEqualTo(requestFromWants ? 0 : 1);
        await Assert.That(sink.StartTagEnded).IsFalse();

        var resumed = tokenizer.WriteUntilYield(utf8.AsSpan(consumed));
        tokenizer.Complete();

        await Assert.That(consumed + resumed).IsEqualTo(utf8.Length);
        await Assert.That(sink.Attributes).IsEquivalentTo(["first=1", "second=2"]);
        await Assert.That(sink.StartTagEnded).IsTrue();
    }

    [Test]
    public async Task YieldRequestedByTextStopsBeforeTheFollowingTagCallback()
    {
        var sink = new YieldingTextSink();
        var tokenizer = new Utf8HtmlTokenizer(sink);
        sink.Tokenizer = tokenizer;
        var utf8 = Encoding.UTF8.GetBytes("text<a first=1>tail");

        var consumed = tokenizer.WriteUntilYield(utf8);

        await Assert.That(consumed).IsLessThan(utf8.Length);
        await Assert.That(sink.StartTags).IsEqualTo(0);

        var resumed = tokenizer.WriteUntilYield(utf8.AsSpan(consumed));
        tokenizer.Complete();

        await Assert.That(consumed + resumed).IsEqualTo(utf8.Length);
        await Assert.That(sink.StartTags).IsEqualTo(1);
        await Assert.That(sink.StartTagEnded).IsTrue();
    }

    [Test]
    public async Task PipeReaderConsumesUtf8WithoutMaterializingWholeResponse()
    {
        var utf8 = Encoding.UTF8.GetBytes(Html);
        var pipe = new Pipe(new PipeOptions(minimumSegmentSize: 8, useSynchronizationContext: false));
        var sink = new RecordingSink();
        var tokenize = Utf8HtmlTokenizer.TokenizeAsync(pipe.Reader, sink).AsTask();

        for (var offset = 0; offset < utf8.Length; offset += 7)
        {
            var length = Math.Min(7, utf8.Length - offset);
            await pipe.Writer.WriteAsync(utf8.AsMemory(offset, length));
        }
        await pipe.Writer.CompleteAsync();

        var counters = await tokenize;
        await pipe.Reader.CompleteAsync();
        var expected = Tokenize(utf8, utf8.Length);

        await Assert.That(sink.Events).IsEquivalentTo(expected.Events);
        await Assert.That(counters.BytesConsumed).IsEqualTo(utf8.Length);
        await Assert.That(counters.MaximumBufferedTokenBytes).IsLessThan(64);
    }

    [Test]
    public async Task KnownLegacyEncodingIsTranscodedIntoTheUtf8Tokenizer()
    {
        const string html = "<main data-label='café'>olá</main>";
        var sourceEncoding = Encoding.Latin1;
        var reader = PipeReader.Create(new MemoryStream(sourceEncoding.GetBytes(html)));
        var sink = new RecordingSink();

        var counters = await EncodedHtmlInput.TokenizeAsync(
            reader,
            HtmlInputEncoding.Known(sourceEncoding),
            sink,
            CancellationToken.None
        );
        var expectedUtf8 = Encoding.UTF8.GetBytes(html);
        var expected = Tokenize(expectedUtf8, expectedUtf8.Length);

        await Assert.That(sink.Events).IsEquivalentTo(expected.Events);
        await Assert.That(counters.BytesConsumed).IsEqualTo(expectedUtf8.Length);
        await reader.CompleteAsync();
    }

    [Test]
    public async Task AutoEncodingUsesMetaBeforeInvokingTheUtf8Tokenizer()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string html = "<meta charset=windows-1252><main>“café”</main>";
        var sourceEncoding = Encoding.GetEncoding(1252);
        var bytes = sourceEncoding.GetBytes(html);
        var expectedUtf8 = Encoding.UTF8.GetBytes(html);
        var expected = Tokenize(expectedUtf8, expectedUtf8.Length);

        foreach (var chunkSize in new[] { 1, 7, bytes.Length })
        {
            var actual = await TokenizeEncoded(bytes, HtmlInputEncoding.Auto(), chunkSize);
            await Assert.That(actual.Events).IsEquivalentTo(expected.Events);
        }
    }

    [Test]
    public async Task AutoEncodingUsesSplitUtf16BomAndPreservesSurrogatePairs()
    {
        const string html = "<main>hello 😀</main>";
        var content = Encoding.Unicode.GetBytes(html);
        var bytes = Encoding.Unicode.GetPreamble().Concat(content).ToArray();
        var expectedUtf8 = Encoding.UTF8.GetBytes(html);
        var expected = Tokenize(expectedUtf8, expectedUtf8.Length);

        var actual = await TokenizeEncoded(bytes, HtmlInputEncoding.Auto(), chunkSize: 1);

        await Assert.That(actual.Events).IsEquivalentTo(expected.Events);
    }

    [Test]
    public async Task OptionalStateMetricsCaptureBulkAndScalarRuns()
    {
        var sink = new RecordingSink();
        var metrics = new Utf8HtmlTokenizerStateMetrics(Utf8HtmlTokenizer.StateCount);
        var tokenizer = new Utf8HtmlTokenizer(sink, metrics);

        tokenizer.Write("ordinary text<p title='value'>tail</p>"u8);
        tokenizer.Complete();

        var states = tokenizer.GetStateMetrics();
        var data = states.Single(metric => metric.State == "Data");
        await Assert.That(data.ByteVisits).IsGreaterThanOrEqualTo(17);
        await Assert.That(data.MaximumRunLength).IsGreaterThanOrEqualTo(13);
        await Assert.That(states.Any(metric => metric.State == "TagName")).IsTrue();
        await Assert.That(states.Any(metric => metric.State == "AttributeValueSingleQuoted")).IsTrue();
    }

    [Test]
    public async Task ScriptDataBulkRunPreservesUtf8AndScalarBoundaries()
    {
        const string html = "<script>ordinary café text\r\nnext\0tail < marker</script><p>after</p>";
        var utf8 = Encoding.UTF8.GetBytes(html);
        var expected = TokenizeWithAngleSharp(html);

        foreach (var segmentSize in new[] { 1, 7, utf8.Length })
            await Assert.That(Tokenize(utf8, segmentSize).Events).IsEquivalentTo(expected);

        var sink = new RecordingSink();
        var metrics = new Utf8HtmlTokenizerStateMetrics(Utf8HtmlTokenizer.StateCount);
        var tokenizer = new Utf8HtmlTokenizer(sink, metrics);
        tokenizer.Write(utf8);
        tokenizer.Complete();

        var scriptData = tokenizer.GetStateMetrics().Single(metric => metric.State == "ScriptData");
        await Assert.That(scriptData.MaximumRunLength).IsGreaterThanOrEqualTo(13);
    }

    [Test]
    public async Task LowercaseTagNameBulkRunPreservesScalarBoundaries()
    {
        const string html = "<ArTiCle><custom-element2></CUSTOM-ELEMENT2><bad\0name/>";
        var utf8 = Encoding.UTF8.GetBytes(html);
        string[] expected =
        [
            "start:article",
            "start-end",
            "start:custom-element2",
            "start-end",
            "end:custom-element2",
            // RecordingSink renders the three UTF-8 replacement bytes through ASCII.
            "start:bad???name",
            "start-end:/",
            "eof",
        ];

        foreach (var segmentSize in new[] { 1, 7, utf8.Length })
            await Assert.That(Tokenize(utf8, segmentSize).Events).IsEquivalentTo(expected);

        var sink = new RecordingSink();
        var metrics = new Utf8HtmlTokenizerStateMetrics(Utf8HtmlTokenizer.StateCount);
        var tokenizer = new Utf8HtmlTokenizer(sink, metrics);
        tokenizer.Write(utf8);
        tokenizer.Complete();

        var tagName = tokenizer.GetStateMetrics().Single(metric => metric.State == "TagName");
        await Assert.That(tagName.MaximumRunLength).IsGreaterThanOrEqualTo(6);
    }

    [Test]
    public async Task QuotedAttributeBulkRunsPreserveScalarBoundaries()
    {
        const string html = "<div a=\"ordinary café &amp; x\r\nnul\0tail\" b='single &copy; café\rz\0'>text</div>";
        var utf8 = Encoding.UTF8.GetBytes(html);
        var expected = TokenizeWithAngleSharp(html);

        foreach (var segmentSize in new[] { 1, 7, utf8.Length })
            await Assert.That(Tokenize(utf8, segmentSize).Events).IsEquivalentTo(expected);
    }

    [Test]
    public async Task UnquotedAttributeBulkRunsPreserveScalarBoundaries()
    {
        const string html =
            "<div data-url=https://example.test/a/long/path?x=1&amp;y=2 class=wide-card\r\n"
            + " data-null=ab\0cd data-weird=a\"b'c<d=e`f data-title=café>x</div>";
        var utf8 = Encoding.UTF8.GetBytes(html);
        var expected = TokenizeWithAngleSharp(html);

        foreach (var segmentSize in new[] { 1, 7, utf8.Length })
            await Assert.That(Tokenize(utf8, segmentSize).Events).IsEquivalentTo(expected);
    }

    [Test]
    public async Task CommentBulkRunsPreserveScalarBoundaries()
    {
        const string html = "<!--ordinary long café comment <nested - dash \0 nul\r\nnext\rlast--><p>after</p>";
        var utf8 = Encoding.UTF8.GetBytes(html);
        var expected = TokenizeWithAngleSharp(html);

        foreach (var segmentSize in new[] { 1, 7, utf8.Length })
            await Assert.That(Tokenize(utf8, segmentSize).Events).IsEquivalentTo(expected);
    }

    [Test]
    [Arguments("<main data-x='a&amp;b'>hé &amp; <b>bold</b></main>")]
    [Arguments("<textarea>a&amp;b</textarea><style>a>b{color:red}</style>")]
    [Arguments("<div a=1 disabled><span>x</span></div><!--tail-->")]
    [Arguments("<script><!--<script>double escaped</script>still script--></script><p>after</p>")]
    [Arguments("<script><!--escaped--></script><p>after</p>")]
    [Arguments("<plaintext>a</plaintext><b>still plaintext</b>")]
    [Arguments("<!DOCTYPE html>")]
    [Arguments("<!DOCTYPE html PUBLIC '-//W3C//DTD HTML 4.01//EN' 'http://www.w3.org/TR/html4/strict.dtd'>")]
    [Arguments("<!DOCTYPE html SYSTEM 'about:legacy-compat'>")]
    [Arguments("<!DOCTYPE>")]
    [Arguments("<!DOCTYPE html PUBLIC 'x'>")]
    [Arguments("<!DOCTYPE html PUBLIC'x'>")]
    [Arguments("<!DOCTYPE html SYSTEM 'x' junk>")]
    [Arguments("<!DOCTYPE html nope>")]
    [Arguments("<!DOCTYPE html PUBLIC 'unterminated")]
    [Arguments("<p>&notin;&notit;&ampx</p>")]
    [Arguments("<a x='&notit;' y='&ampx'>x</a>")]
    [Arguments("<a x='&#x2F;a&#x3D;1&amp;b=2' y=/a&#x3D;1&amp;b=2>x</a>")]
    [Arguments("<a x='&#0;&#xD800;&#x80;' y='&#12foo &#x12zoo'>x</a>")]
    [Arguments("<a x='&#9999999999999999999999;' y='&#xFFFFFFFFFFFFFFFF;'>x</a>")]
    // A bare "&;" is excluded: AngleSharp drops the ';' where the spec (and this tokenizer,
    // before and after deferred attribute decoding) flushes '&' and keeps ';' as a value byte.
    [Arguments("<a x='&' z='&#' q='&#;' r='&#x' s='&#x;' t='a&'>x</a>")]
    [Arguments("<a x='&not' y='&not;in' z='&notq' w=a&ampz>x</a>")]
    [Arguments("<a x='&&amp;&#38&' y='&aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;'>x</a>")]
    [Arguments("<p>&#x80;&#0;&#xD800;</p>")]
    [Arguments("<p>&#12foo &#x12zoo</p>")]
    [Arguments("<p>&#99999999999999999999999999999999999999999999999999;</p>")]
    [Arguments("<p>&#xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF;</p>")]
    [Arguments("<p>&#xFDD0;&#x1F;&#13;</p>")]
    public async Task CommonReadOnlyLexicalPathMatchesAngleSharp(string html)
    {
        var utf8 = Encoding.UTF8.GetBytes(html);
        var actual = Tokenize(utf8, 3).Events;
        var expected = TokenizeWithAngleSharp(html);

        await Assert.That(actual).IsEquivalentTo(expected);
    }

    [Test]
    [Arguments("<script><!--<script>double escaped</script>still script--></script><p>after</p>")]
    [Arguments("<plaintext>a</plaintext><b>still plaintext</b>")]
    public async Task StatefulTextModesMatchAtEveryByteBoundary(string html)
    {
        var utf8 = Encoding.UTF8.GetBytes(html);
        var bytewise = Tokenize(utf8, 1).Events;
        var expected = TokenizeWithAngleSharp(html);

        await Assert.That(bytewise).IsEquivalentTo(expected);
    }

    [Test]
    [Property("Issue", "78")]
    public async Task DiscardedEndTagTailConsumesTheFirstMalformedAttributeNameByte()
    {
        // Regression found by the differential mutator (seed 20260807, iteration 18177).
        // CaptureOff used to leave '=' unconsumed after the unexpected '/', reinterpret the
        // following quote as an attribute-value opener, and discard the complete end tag at EOF.
        const string html = "</a/=\">";
        var utf8 = Encoding.UTF8.GetBytes(html);
        var expected = TokenizeWithAngleSharp(html);

        await Assert.That(expected).IsEquivalentTo(["end:a", "eof"]);
        foreach (var segmentSize in new[] { 1, 2, 3, utf8.Length })
            await Assert.That(Tokenize(utf8, segmentSize).Events).IsEquivalentTo(expected);
    }

    [Test]
    [Property("Issue", "76")]
    [Arguments("<script>a<")]
    [Arguments("<script>a</scr")]
    [Arguments("<script><!--a<")]
    [Arguments("<script><!--a</scr")]
    public async Task PendingScriptCandidatesAreFlushedAtEof(string html)
    {
        var utf8 = Encoding.UTF8.GetBytes(html);
        var expected = TokenizeWithAngleSharp(html);

        foreach (var segmentSize in new[] { 1, 2, 3, utf8.Length })
            await Assert.That(Tokenize(utf8, segmentSize).Events).IsEquivalentTo(expected);
    }

    [Test]
    public async Task InvalidUtf8IsReplacedBeforeBorrowedCallbacks()
    {
        byte[][] cases =
        [
            [0x61, 0x80, 0x62],
            [0xC0, 0xAF],
            [0xE2, 0x82],
            [0xED, 0xA0, 0x80],
            [0xF4, 0x90, 0x80, 0x80],
        ];

        foreach (var utf8 in cases)
        {
            var actual = Tokenize(utf8, 1).Events;
            var expected = TokenizeWithAngleSharp(Encoding.UTF8.GetString(utf8));
            await Assert.That(actual).IsEquivalentTo(expected);
        }
    }

    [Test]
    public async Task DanglingMalformedCarryMatchesEveryChunking()
    {
        // Fuzz-found (seed 214748): a carried pair like E0 87 is already malformed - 87 is
        // outside E0's valid continuation range - and end-of-stream replacement must produce two
        // U+FFFD, exactly as if more input had arrived. Collapsing the whole carry into a single
        // replacement made the token stream depend on input chunking.
        byte[][] cases =
        [
            [0xE0, 0x87],
            [0x61, 0xE0, 0x87],
            [0xF5, 0x8F],
            [0xE4, 0xB8],
            [0xF4, 0x8F],
            [0xF0, 0x9F, 0x98],
        ];

        foreach (var utf8 in cases)
        {
            var expected = TokenizeWithAngleSharp(Encoding.UTF8.GetString(utf8));
            foreach (var segmentSize in new[] { 1, 2, 3, utf8.Length })
            {
                var actual = Tokenize(utf8, segmentSize).Events;
                await Assert.That(actual).IsEquivalentTo(expected);
            }
        }
    }

    [Test]
    [Arguments(1)]
    [Arguments(7)]
    public async Task CDataDeclarationRequiresForeignContentSignal(int segmentSize)
    {
        var utf8 = "<![CDATA[a]]>b"u8.ToArray();

        var foreignSink = new RecordingSink();
        var foreignTokenizer = new Utf8HtmlTokenizer(foreignSink) { IsAcceptingCharacterData = true };
        for (var offset = 0; offset < utf8.Length; offset += segmentSize)
            foreignTokenizer.Write(utf8.AsMemory(offset, Math.Min(segmentSize, utf8.Length - offset)));
        foreignTokenizer.Complete();

        var html = Tokenize(utf8, segmentSize).Events;
        await Assert.That(foreignSink.Events).IsEquivalentTo(["text:ab", "eof"]);
        await Assert.That(html).IsEquivalentTo(["comment:[CDATA[a]]", "text:b", "eof"]);
    }

    private static (IReadOnlyList<string> Events, Utf8HtmlTokenizerCounters Counters) Tokenize(
        byte[] utf8,
        int segmentSize
    )
    {
        var sink = new RecordingSink();
        var tokenizer = new Utf8HtmlTokenizer(sink);
        var input = new Utf8HtmlTokenizerInput(tokenizer);
        for (var offset = 0; offset < utf8.Length; offset += segmentSize)
            input.Write(utf8.AsMemory(offset, Math.Min(segmentSize, utf8.Length - offset)));
        input.Complete();
        return (sink.Events, input.Counters);
    }

    private static async Task<(IReadOnlyList<string> Events, Utf8HtmlTokenizerCounters Counters)> TokenizeEncoded(
        byte[] source,
        HtmlInputEncoding inputEncoding,
        int chunkSize
    )
    {
        var pipe = new Pipe(new PipeOptions(minimumSegmentSize: 8, useSynchronizationContext: false));
        var sink = new RecordingSink();
        var tokenization = EncodedHtmlInput
            .TokenizeAsync(pipe.Reader, inputEncoding, sink, CancellationToken.None)
            .AsTask();

        for (var offset = 0; offset < source.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, source.Length - offset);
            await pipe.Writer.WriteAsync(source.AsMemory(offset, length));
        }
        await pipe.Writer.CompleteAsync();

        var counters = await tokenization;
        await pipe.Reader.CompleteAsync();
        return (sink.Events, counters);
    }

    private static IReadOnlyList<string> TokenizeWithAngleSharp(string html)
    {
        var sink = new RecordingSink();
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
                        "title" or "textarea" => AngleSharp.Html.Parser.HtmlParseMode.RCData,
                        "style" or "xmp" or "iframe" or "noembed" or "noframes" => AngleSharp
                            .Html
                            .Parser
                            .HtmlParseMode
                            .Rawtext,
                        "script" => AngleSharp.Html.Parser.HtmlParseMode.Script,
                        "plaintext" => AngleSharp.Html.Parser.HtmlParseMode.Plaintext,
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
                    return sink.Events;
            }
        }
    }

    private sealed class RecordingSink : IUtf8HtmlTokenSink
    {
        public Utf8HtmlTokenCapture Capture => Utf8HtmlTokenCapture.Text;

        private readonly List<string> _events = [];

        public IReadOnlyList<string> Events => _events;

        public void Text(ReadOnlySpan<byte> utf8)
        {
            EnsureValidUtf8(utf8);
            var text = Encoding.UTF8.GetString(utf8);
            if (_events.Count != 0 && _events[^1].StartsWith("text:", StringComparison.Ordinal))
                _events[^1] += text;
            else
                _events.Add("text:" + text);
        }

        public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name)
        {
            StartTag(name.Verbatim);
            return Utf8HtmlStartTagCapture.Attributes;
        }

        public void StartTag(ReadOnlySpan<byte> name) => _events.Add("start:" + DecodeSemanticName(name));

        public bool WantsAttribute(Utf8HtmlName name) => true;

        public void Attribute(Utf8HtmlName name, ReadOnlySpan<byte> value) => Attribute(name.Verbatim, value);

        public void Attribute(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value) =>
            _events.Add("attr:" + DecodeSemanticName(name) + "=" + DecodeValidUtf8(value));

        public void StartTagEnd(bool selfClosing) => _events.Add(selfClosing ? "start-end:/" : "start-end");

        public void EndTag(Utf8HtmlName name) => EndTag(name.Verbatim);

        public void EndTag(ReadOnlySpan<byte> name) => _events.Add("end:" + DecodeSemanticName(name));

        private static string DecodeSemanticName(ReadOnlySpan<byte> name)
        {
            var bytes = name.ToArray();
            for (var index = 0; index < bytes.Length; index++)
            {
                var value = bytes[index];
                if ((uint)(value - (byte)'A') <= 'Z' - 'A')
                    bytes[index] = (byte)(value | 0x20);
            }
            return Encoding.ASCII.GetString(bytes);
        }

        public void Comment(ReadOnlySpan<byte> utf8) => _events.Add("comment:" + Encoding.UTF8.GetString(utf8));

        public void Doctype(ReadOnlySpan<byte> utf8) => _events.Add("doctype:" + Encoding.ASCII.GetString(utf8));

        public void Doctype(in Utf8DoctypeToken token) =>
            _events.Add(
                $"doctype:{Encoding.UTF8.GetString(token.Name)}|"
                    + $"{token.IsPublicIdentifierMissing}:{Encoding.UTF8.GetString(token.PublicIdentifier)}|"
                    + $"{token.IsSystemIdentifierMissing}:{Encoding.UTF8.GetString(token.SystemIdentifier)}|"
                    + token.IsQuirksForced
            );

        public void EndOfFile() => _events.Add("eof");

        private static string DecodeValidUtf8(ReadOnlySpan<byte> utf8)
        {
            EnsureValidUtf8(utf8);
            return Encoding.UTF8.GetString(utf8);
        }

        private static void EnsureValidUtf8(ReadOnlySpan<byte> utf8)
        {
            while (!utf8.IsEmpty)
            {
                if (Rune.DecodeFromUtf8(utf8, out _, out var consumed) != OperationStatus.Done)
                    throw new InvalidDataException("Tokenizer emitted invalid UTF-8.");
                utf8 = utf8[consumed..];
            }
        }
    }

    [Test]
    [Arguments(1)]
    [Arguments(4)]
    [Arguments(Int32.MaxValue)]
    public async Task StartTagAttributeFilterSkipsExactlyTheFilterMissedNames(Int32 chunkSize)
    {
        var wantedBit = Utf8NameHash.AttributeFilterBit(Utf8NameHash.ComputeSemantic("href"u8));
        // Derived from the current hash so the shapes stay constructible if the function changes:
        // 'missed' can never intersect the wanted filter, 'collider' shares href's exact bit
        // without being semantically equal to it (a guaranteed bloom false positive).
        var missed = FindAttributeName(bit => (bit & wantedBit) == 0);
        var collider = FindAttributeName(bit => bit == wantedBit);
        var sink = new FilteringAttributeSink { StartTagAttributeFilter = wantedBit };
        var tokenizer = new Utf8HtmlTokenizer(sink);
        var utf8 = Encoding.UTF8.GetBytes(
            $"<a {missed}=m1 {collider}=c1 href=first {missed}=m2 HREF=second {collider}=c2>"
        );

        for (var offset = 0; offset < utf8.Length; )
        {
            var length = Math.Min(chunkSize, utf8.Length - offset);
            tokenizer.Write(utf8.AsSpan(offset, length));
            offset += length;
        }
        tokenizer.Complete();

        // Filter-missed names never reach the sink; false positives and duplicates (including the
        // respelled HREF) fall through to the exact WantsAttribute path; first occurrence wins.
        await Assert.That(String.Join('|', sink.WantsAsked)).IsEqualTo($"{collider}|href|href|{collider}");
        await Assert.That(String.Join('|', sink.Captured)).IsEqualTo("href=first");
    }

    private static String FindAttributeName(Func<UInt64, Boolean> predicate)
    {
        Span<Byte> candidate = stackalloc Byte[3];
        candidate[0] = (Byte)'x';
        for (var first = (Byte)'a'; first <= (Byte)'z'; first++)
        {
            for (var second = (Byte)'a'; second <= (Byte)'z'; second++)
            {
                candidate[1] = first;
                candidate[2] = second;
                if (predicate(Utf8NameHash.AttributeFilterBit(Utf8NameHash.ComputeSemantic(candidate))))
                {
                    return Encoding.ASCII.GetString(candidate);
                }
            }
        }
        throw new InvalidOperationException("No candidate attribute name matched the required filter-bit shape.");
    }

    private sealed class FilteringAttributeSink : IUtf8HtmlTokenSink
    {
        public Utf8HtmlTokenCapture Capture => Utf8HtmlTokenCapture.None;

        public UInt64 StartTagAttributeFilter { get; init; }

        public List<String> WantsAsked { get; } = [];

        public List<String> Captured { get; } = [];

        public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name) => Utf8HtmlStartTagCapture.Attributes;

        public Boolean WantsAttribute(Utf8HtmlName name)
        {
            WantsAsked.Add(Encoding.ASCII.GetString(name.Verbatim).ToLowerInvariant());
            return name.SemanticEquals("href"u8);
        }

        public void Attribute(Utf8HtmlName name, ReadOnlySpan<Byte> value) =>
            Captured.Add(
                $"{Encoding.ASCII.GetString(name.Verbatim).ToLowerInvariant()}={Encoding.UTF8.GetString(value)}"
            );

        public void StartTagEnd(Boolean selfClosing) { }

        public void Text(ReadOnlySpan<Byte> utf8) { }

        public void EndTag(Utf8HtmlName name) { }
    }

    private sealed class YieldingAttributeSink(Boolean requestFromWants) : IUtf8HtmlTokenSink
    {
        private Boolean _requested;

        public Utf8HtmlTokenizer Tokenizer { get; set; } = null!;

        public List<String> Attributes { get; } = [];

        public Boolean StartTagEnded { get; private set; }

        public Utf8HtmlTokenCapture Capture => Utf8HtmlTokenCapture.None;

        public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name) => Utf8HtmlStartTagCapture.Attributes;

        public Boolean WantsAttribute(Utf8HtmlName name)
        {
            if (requestFromWants && !_requested)
            {
                _requested = true;
                Tokenizer.RequestYield();
            }
            return true;
        }

        public void Attribute(Utf8HtmlName name, ReadOnlySpan<Byte> value)
        {
            Attributes.Add($"{Encoding.UTF8.GetString(name.Verbatim)}={Encoding.UTF8.GetString(value)}");
            if (!requestFromWants && !_requested)
            {
                _requested = true;
                Tokenizer.RequestYield();
            }
        }

        public void StartTagEnd(Boolean selfClosing) => StartTagEnded = true;

        public void Text(ReadOnlySpan<Byte> utf8) { }

        public void EndTag(Utf8HtmlName name) { }

        public void Comment(ReadOnlySpan<Byte> utf8) { }

        public void ProcessingInstruction(ReadOnlySpan<Byte> utf8) { }

        public void Doctype(in Utf8DoctypeToken token) { }

        public void EndOfFile() { }
    }

    private sealed class YieldingTextSink : IUtf8HtmlTokenSink
    {
        private Boolean _requested;

        public Utf8HtmlTokenizer Tokenizer { get; set; } = null!;

        public Int32 StartTags { get; private set; }

        public Boolean StartTagEnded { get; private set; }

        public Utf8HtmlTokenCapture Capture => Utf8HtmlTokenCapture.Text;

        public void Text(ReadOnlySpan<Byte> utf8)
        {
            if (_requested)
            {
                return;
            }
            _requested = true;
            Tokenizer.RequestYield();
        }

        public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name)
        {
            StartTags++;
            return Utf8HtmlStartTagCapture.None;
        }

        public Boolean WantsAttribute(Utf8HtmlName name) => false;

        public void Attribute(Utf8HtmlName name, ReadOnlySpan<Byte> value) { }

        public void StartTagEnd(Boolean selfClosing) => StartTagEnded = true;

        public void EndTag(Utf8HtmlName name) { }

        public void Comment(ReadOnlySpan<Byte> utf8) { }

        public void ProcessingInstruction(ReadOnlySpan<Byte> utf8) { }

        public void Doctype(in Utf8DoctypeToken token) { }

        public void EndOfFile() { }
    }
}
#endif
