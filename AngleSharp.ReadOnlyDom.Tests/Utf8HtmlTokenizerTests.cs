#if NET10_0
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens;
using AngleSharp.Text;
using AngleSharp.ReadOnlyDom.Streaming;

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
        await Assert.That(bytewise.Counters.MaximumSourceLookbehind).IsEqualTo(0);
        await Assert.That(bytewise.Counters.InputSegments).IsEqualTo(utf8.Length);
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
        await Assert.That(counters.MaximumSourceLookbehind).IsEqualTo(0);
        await Assert.That(counters.MaximumBufferedTokenBytes).IsLessThan(64);
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
        for (var offset = 0; offset < utf8.Length; offset += segmentSize)
            tokenizer.Write(utf8.AsMemory(offset, Math.Min(segmentSize, utf8.Length - offset)));
        tokenizer.Complete();
        return (sink.Events, tokenizer.Counters);
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
                    return sink.Events;
            }
        }
    }

    private sealed class RecordingSink : IUtf8HtmlTokenSink
    {
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

        public void StartTag(ReadOnlySpan<byte> name) => _events.Add("start:" + Encoding.ASCII.GetString(name));

        public void Attribute(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value) =>
            _events.Add("attr:" + Encoding.ASCII.GetString(name) + "=" + DecodeValidUtf8(value));

        public void StartTagEnd(bool selfClosing) => _events.Add(selfClosing ? "start-end:/" : "start-end");

        public void EndTag(ReadOnlySpan<byte> name) => _events.Add("end:" + Encoding.ASCII.GetString(name));

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
}
#endif
