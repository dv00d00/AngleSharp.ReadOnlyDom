#if NET10_0
using System.Buffers;
using System.Text;
using System.IO.Pipelines;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.ReadOnlyDom.Streaming.Internal;
using AngleSharp.ReadOnlyDom.Streaming.Public;
using AngleSharp.ReadOnlyDom.Streaming.Utf8Tokenizer;

namespace AngleSharp.Readonly.Tests;

public sealed class QueryTests
{
    [Test]
    public async Task ChildAndDescendantRelationsRemainDistinct()
    {
        var root = StreamQuery.For<QueryState>("main");
        root.Child("a").OnStart(static (ref QueryState state, in Element _) => state.Events.Add("child"));
        root.Descendant("a").OnStart(static (ref QueryState state, in Element _) => state.Events.Add("descendant"));

        var state = root.Compile()
            .Execute("<main><a>direct</a><section><a>nested</a></section></main>"u8, new QueryState());

        await Assert.That(string.Join('|', state.Events)).IsEqualTo("child|descendant|descendant");
    }

    [Test]
    public async Task PredicatesAndProjectedAttributesUseBorrowedUtf8()
    {
        var root = QueryNode<QueryState>
            .Root(
                Selector
                    .Tag("article")
                    .WithId("story")
                    .WithClass("featured")
                    .WithAttribute("data-id")
                    .WithAttribute("lang", "en")
            )
            .OnStart(
                static (ref QueryState state, in Element element) =>
                {
                    state.Events.Add(Encoding.UTF8.GetString(Get(element, "data-id")));
                    state.Events.Add(element.HasAttribute("missing") ? "present" : "missing");
                },
                "data-id",
                "missing"
            );

        var state = root.Compile()
            .Execute(
                "<article id=story class='lead featured wide' data-id='42' lang=en></article>"u8,
                new QueryState()
            );

        await Assert.That(string.Join('|', state.Events)).IsEqualTo("42|missing");
    }

    [Test]
    public async Task StartHandlersSeeOnlyTheirOwnProjectedAttributesWithAsciiInsensitiveStringLookup()
    {
        var root = StreamQuery.For<QueryState>("main");
        root.Descendant("a")
            .OnStart(
                static (ref QueryState state, in Element element) =>
                    state.Events.Add(
                        $"first:{element.TryGetAttribute("HREF", out _)}:{element.TryGetAttribute("title", out _)}"
                    ),
                "href"
            );
        root.Descendant("a")
            .OnStart(
                static (ref QueryState state, in Element element) =>
                    state.Events.Add(
                        $"second:{element.TryGetAttribute("href", out _)}:{element.TryGetAttribute("TITLE", out _)}"
                    ),
                "title"
            );

        var state = root.Compile().Execute("<main><a href=/item title=Item></a></main>"u8, new QueryState());

        await Assert.That(state.Events).IsEquivalentTo(["first:True:False", "second:False:True"]);
    }

    [Test]
    public async Task RepeatedLowLevelHandlersAndNullProjectionArraysAreRejected()
    {
        var start = StreamQuery.For<QueryState>("div").OnStart(static (ref QueryState _, in Element _) => { });
        var text = StreamQuery.For<QueryState>("div").OnText(static (ref QueryState _, ReadOnlySpan<byte> _) => { });
        var end = StreamQuery.For<QueryState>("div").OnEnd(static (ref QueryState _) => { });

        await Assert
            .That(() => start.OnStart(static (ref QueryState _, in Element _) => { }))
            .Throws<InvalidOperationException>();
        await Assert
            .That(() => text.OnText(static (ref QueryState _, ReadOnlySpan<byte> _) => { }))
            .Throws<InvalidOperationException>();
        await Assert.That(() => end.OnEnd(static (ref QueryState _) => { })).Throws<InvalidOperationException>();
        await Assert
            .That(() =>
                StreamQuery.For<QueryState>("div").OnStart(static (ref QueryState _, in Element _) => { }, null!)
            )
            .Throws<ArgumentNullException>();
        await Assert
            .That(() =>
                StreamQuery
                    .For<QueryState>("div")
                    .OnClose(static (ref QueryState _, in CompletedElement _) => { }, null!)
            )
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task SelectorNamesRejectOnlyUnsupportedAsciiDelimitersAndControls()
    {
        foreach (var name in new[] { "a b", "a/b", "a>b", "a\0b", "a\u0001b", "a\u007Fb" })
        {
            await Assert.That(() => StreamQuery.For<QueryState>(name)).Throws<ArgumentException>();
            await Assert.That(() => StreamQuery.For<QueryState>("div").Attribute(name)).Throws<ArgumentException>();
        }

        StreamQuery.For<QueryState>("a=b").Attribute("data'value").Compile();
        await Assert.That(() => StreamQuery.For<QueryState>("div").Attribute("data=value")).Throws<ArgumentException>();
    }

    [Test]
    public async Task MixedCaseCompactAndFallbackNamesPreserveFirstDuplicateAttribute()
    {
        var root = StreamQuery
            .For<QueryState>("article")
            .Attribute("id", "first")
            .Attribute("class", "card")
            .Attribute("data-key", "one")
            .Attribute("title", "title")
            .OnStart(
                static (ref QueryState state, in Element element) =>
                    state.Events.Add(
                        String.Join(
                            "|",
                            Encoding.UTF8.GetString(Get(element, "id")),
                            Encoding.UTF8.GetString(Get(element, "class")),
                            Encoding.UTF8.GetString(Get(element, "data-key")),
                            Encoding.UTF8.GetString(Get(element, "title"))
                        )
                    ),
                "id",
                "class",
                "data-key",
                "title"
            );

        var state = root.Compile()
            .Execute(
                Encoding.UTF8.GetBytes(
                    "<ArTiClE ID='first' id='ignored' CLASS='card' class='ignored' "
                        + "DaTa-Key='one' data-key='ignored' TITLE='title' title='ignored'></ArTiClE>"
                ),
                new QueryState()
            );

        await Assert.That(state.Events).IsEquivalentTo(["first|card|one|title"]);
    }

    [Test]
    public async Task TextEndAndVoidCallbacksPreserveStructuralOrderAcrossSegments()
    {
        var root = StreamQuery.For<QueryState>("main");
        root.Descendant("a")
            .OnStart(static (ref QueryState state, in Element _) => state.Events.Add("a:start"))
            .OnText(
                static (ref QueryState state, ReadOnlySpan<byte> text) =>
                    state.Text.Append(Encoding.UTF8.GetString(text))
            )
            .OnEnd(static (ref QueryState state) => state.Events.Add("a:end"))
            .Descendant("img")
            .OnStart(static (ref QueryState state, in Element _) => state.Events.Add("img:start"))
            .OnEnd(static (ref QueryState state) => state.Events.Add("img:end"));

        var plan = root.Compile();
        var state = new QueryState();
        using (var execution = plan.CreateExecution(state))
        {
            var tokenizer = new Utf8HtmlTokenizer(execution);
            var html = "<main><a>hello <b>bold</b><img></a></main>"u8;
            for (var index = 0; index < html.Length; index++)
                tokenizer.Write(html.Slice(index, 1));
            tokenizer.Complete();
        }

        await Assert.That(string.Join('|', state.Events)).IsEqualTo("a:start|img:start|img:end|a:end");
        await Assert.That(state.Text.ToString()).IsEqualTo("hello bold");
    }

    [Test]
    public async Task PipeReaderFeedsTheSameCompiledPlanIncrementally()
    {
        var root = StreamQuery
            .For<QueryState>("main")
            .OnText(
                static (ref QueryState state, ReadOnlySpan<byte> text) =>
                    state.Text.Append(Encoding.UTF8.GetString(text))
            );
        var pipe = new Pipe(new PipeOptions(minimumSegmentSize: 8, useSynchronizationContext: false));
        var execute = root.Compile().ExecuteAsync(pipe.Reader, new QueryState()).AsTask();

        await pipe.Writer.WriteAsync("<main>hé"u8.ToArray());
        await pipe.Writer.WriteAsync("llo</main>"u8.ToArray());
        await pipe.Writer.CompleteAsync();
        var state = await execute;
        await pipe.Reader.CompleteAsync();

        await Assert.That(state.Text.ToString()).IsEqualTo("héllo");
    }

    [Test]
    public async Task PipeReaderPreservesRuneBoundariesForCompletedNormalizedText()
    {
        var root = StreamQuery
            .For<QueryState>("p")
            .OnNormalizedText(
                static (ref QueryState state, in CompletedElement element) => state.Events.Add(element.GetText())
            );
        var pipe = new Pipe(new PipeOptions(minimumSegmentSize: 1, useSynchronizationContext: false));
        var execute = root.Compile().ExecuteAsync(pipe.Reader, new QueryState()).AsTask();
        var html = "<p>A\u00a0B © C</p>"u8.ToArray();

        foreach (var value in html)
            await pipe.Writer.WriteAsync(new byte[] { value });
        await pipe.Writer.CompleteAsync();
        var state = await execute;
        await pipe.Reader.CompleteAsync();

        await Assert.That(state.Events).IsEquivalentTo(["A B © C"]);
    }

    [Test]
    public async Task SynchronousExecutionRepairsMalformedUtf8ByDefaultAndAllowsExplicitTrust()
    {
        var root = StreamQuery
            .For<QueryState>("p")
            .OnText(
                static (ref QueryState state, ReadOnlySpan<byte> text) =>
                    state.Text.Append(Encoding.UTF8.GetString(text))
            );
        var plan = root.Compile();
        byte[] malformed = [.. "<p>A"u8, 0xC2, .. "B</p>"u8];

        var repaired = plan.Execute(malformed, new QueryState());
        var trusted = plan.Execute("<p>A\u00a0B</p>"u8, new QueryState(), Utf8InputContract.WellFormedUtf8);

        await Assert.That(repaired.Text.ToString()).IsEqualTo("A\uFFFDB");
        await Assert.That(trusted.Text.ToString()).IsEqualTo("A\u00a0B");
    }

    [Test]
    public async Task CompactTagIdentityMatchesNormalizedNamesAcrossChunks()
    {
        var root = StreamQuery
            .For<QueryState>("article")
            .OnStart(static (ref QueryState state, in Element _) => state.Events.Add("start"))
            .OnEnd(static (ref QueryState state) => state.Events.Add("end"));
        var plan = root.Compile();
        var state = new QueryState();
        using (var execution = plan.CreateExecution(state))
        {
            var tokenizer = new Utf8HtmlTokenizer(execution);
            var html = "<ArTiClE></aRtIcLe>"u8;
            foreach (var value in html)
                tokenizer.Write([value]);
            tokenizer.Complete();
        }

        await Assert.That(state.Events).IsEquivalentTo(["start", "end"]);
    }

    [Test]
    public async Task NonCompactTagIdentityFallsBackToCaseInsensitiveBytes()
    {
        var root = StreamQuery
            .For<QueryState>("custom-element")
            .OnStart(static (ref QueryState state, in Element _) => state.Events.Add("start"))
            .OnEnd(static (ref QueryState state) => state.Events.Add("end"));
        var state = root.Compile()
            .Execute("<CuStOm-ElEmEnT></cUsToM-eLeMeNt>"u8, new QueryState(), Utf8InputContract.WellFormedUtf8);

        await Assert.That(state.Events).IsEquivalentTo(["start", "end"]);
    }

    [Test]
    public async Task QueryTokenizerDoesNotMaterializeUnrequestedAttributeValues()
    {
        var root = StreamQuery
            .For<QueryState>("article")
            .Id("story")
            .OnStart(static (ref QueryState state, in Element _) => state.Events.Add("match"));
        var state = new QueryState();
        Utf8HtmlTokenizerCounters counters;
        using (var execution = root.Compile().CreateExecution(state))
        {
            var tokenizer = new Utf8HtmlTokenizer(execution);
            tokenizer.Write(Encoding.UTF8.GetBytes($"<article ignored='{new string('x', 8192)}' id=story></article>"));
            tokenizer.Complete();
            counters = tokenizer.Counters;
        }

        await Assert.That(state.Events).IsEquivalentTo(["match"]);
        await Assert.That(counters.MaximumBufferedTokenBytes).IsLessThan(256);
    }

    [Test]
    public async Task QueryTokenizerDoesNotCaptureAttributeSyntaxForIrrelevantTags()
    {
        var root = StreamQuery
            .For<QueryState>("article")
            .Id("story")
            .OnStart(static (ref QueryState state, in Element _) => state.Events.Add("match"));
        var state = new QueryState();
        var ignoredName = new string('a', 4096);
        var html = Encoding.UTF8.GetBytes(
            $"<aside {ignoredName}='&amp;&#x1F642;&notit;\r\n{new string('x', 4096)}'>"
                + "<article id=story></article></aside>"
        );
        Utf8HtmlTokenizerCounters counters;
        using (var execution = root.Compile().CreateExecution(state))
        {
            var tokenizer = new Utf8HtmlTokenizer(execution);
            foreach (var value in html)
                tokenizer.Write([value]);
            tokenizer.Complete();
            counters = tokenizer.Counters;
        }

        await Assert.That(state.Events).IsEquivalentTo(["match"]);
        await Assert.That(counters.MaximumBufferedTokenBytes).IsLessThan(256);
    }

    [Test]
    public async Task CompletedElementCallbackCapturesNestedTextAndAttributesOnce()
    {
        var root = StreamQuery.For<QueryState>("main");
        root.Descendant("section")
            .Attribute("data-id")
            .OnNormalizedText(
                static (ref state, in element) =>
                {
                    if (!element.TryGetAttributeUtf8("data-id", out var id))
                        throw new InvalidOperationException("Projected data-id is missing.");
                    state.Events.Add($"{Encoding.UTF8.GetString(id)}:{element.GetText()}");
                },
                "title"
            );
        root.Descendant("img")
            .OnClose(
                static (ref state, in element) => state.Events.Add($"img:{element.GetAttributeOrEmpty("alt")}"),
                "alt"
            );

        var state = root.Compile()
            .Execute(
                "<main><section data-id=outer> Outer <section data-id=inner> Inner </section> tail </section><img alt=Logo></main>"u8,
                new QueryState()
            );

        await Assert.That(string.Join('|', state.Events)).IsEqualTo("inner:Inner|outer:Outer Inner tail|img:Logo");
    }

    [Test]
    public async Task CompletedElementBorrowsUtf8AndDecodesOnlyOnRequest()
    {
        var query = StreamQuery
            .For<QueryState>("p")
            .Attribute("data-label")
            .OnNormalizedText(
                static (ref state, in element) =>
                {
                    if (!element.TextUtf8.SequenceEqual("hé llo"u8))
                        throw new InvalidOperationException("Normalized UTF-8 text disagrees.");
                    if (!element.TryGetAttributeUtf8("data-label"u8, out var label) || !label.SequenceEqual("café"u8))
                        throw new InvalidOperationException("Borrowed UTF-8 attribute disagrees.");
                    if (element.TryGetAttributeUtf8("missing"u8, out _))
                        throw new InvalidOperationException("Missing attribute was reported as present.");
                    state.Events.Add($"{element.GetAttribute("data-label")}:{element.GetText()}");
                }
            );

        var state = query.Compile().Execute("<p data-label='café'> hé\u00a0llo </p>"u8, new QueryState());

        await Assert.That(state.Events).IsEquivalentTo(["café:hé llo"]);
    }

    [Test]
    public async Task MalformedTableNestingDocumentsTheLexicalTopologyBoundary()
    {
        const string html = "<table><div id=inside>lexically nested</div></table>";
        var root = StreamQuery.For<QueryState>("table");
        root.Descendant("div")
            .Id("inside")
            .OnStart(static (ref QueryState state, in Element _) => state.Events.Add("match"));
        var lexical = root.Compile().Execute(Encoding.UTF8.GetBytes(html), new QueryState());

        using var browserDocument = new HtmlParser().ParseDocument(html);

        await Assert.That(lexical.Events.Count).IsEqualTo(1);
        await Assert.That(browserDocument.QuerySelector("table #inside")).IsNull();
    }

    [Test]
    public async Task QueryNodeLimitIsEnforced()
    {
        var root = StreamQuery.For<QueryState>("ul").Class("news-list");
        root.Descendant("a").Attribute("href").OnStart(static (ref QueryState _, in Element _) => { }, "title");

        var node = root;
        for (var index = 0; index < 63; index++)
            node = node.Descendant("div");
        var rejected = false;
        try
        {
            root.Compile();
        }
        catch (NotSupportedException)
        {
            rejected = true;
        }
        await Assert.That(rejected).IsTrue();
    }

    [Test]
    public async Task RewritePreservesUntouchedUtf8AndEditsOnlyTerminalMatches()
    {
        var root = StreamQuery.For<int>("main");
        root.Descendant("a").Attribute("href");
        var source = "<main>é<!--keep--><a href='x'>text</a><a href=y /></main>"u8;
        var output = new ArrayBufferWriter<byte>();

        var matches = root.Compile()
            .Rewrite(
                source,
                output,
                0,
                static (ref int count, in Element _, ref StartTagEditor tag) =>
                {
                    count++;
                    tag.AppendAttribute("data-query-hit"u8, "1"u8);
                },
                Utf8InputContract.WellFormedUtf8
            );

        await Assert.That(matches).IsEqualTo(2);
        await Assert
            .That(Encoding.UTF8.GetString(output.WrittenSpan))
            .IsEqualTo(
                "<main>é<!--keep--><a href='x' data-query-hit=\"1\">text</a><a href=y data-query-hit=\"1\"/></main>"
            );
    }

    [Test]
    public async Task RewriteDoesNotMaterializeDiscardedCommentPayloads()
    {
        var comment = new string('x', 4096);
        var source = Encoding.UTF8.GetBytes($"<main><!--{comment}--><a href=x>text</a></main>");
        var expected = Encoding.UTF8.GetBytes($"<main><!--{comment}--><a href=x data-query-hit=\"1\">text</a></main>");
        var output = new ArrayBufferWriter<byte>();
        var limits = new HtmlStreamingLimits(maximumBufferedTokenBytes: 64);
        var query = StreamQuery.For<int>("a").Attribute("href").Compile();

        var matches = query.Rewrite(
            source,
            output,
            0,
            static (ref int count, in Element _, ref StartTagEditor tag) =>
            {
                count++;
                tag.AppendAttribute("data-query-hit"u8, "1"u8);
            },
            Utf8InputContract.WellFormedUtf8,
            limits
        );

        await Assert.That(matches).IsEqualTo(1);
        await Assert.That(output.WrittenSpan.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task RewriteEscapesAttributeValueAndRejectsMalformedUtf8ByDefault()
    {
        var query = StreamQuery.For<int>("a").Compile();
        var output = new ArrayBufferWriter<byte>();
        query.Rewrite(
            "<a>"u8,
            output,
            0,
            static (ref int _, in Element _, ref StartTagEditor tag) => tag.AppendAttribute("data-value"u8, "a&\"b"u8)
        );
        await Assert.That(Encoding.UTF8.GetString(output.WrittenSpan)).IsEqualTo("<a data-value=\"a&amp;&quot;b\">");

        var rejected = false;
        output = new ArrayBufferWriter<byte>();
        try
        {
            query.Rewrite(
                [(byte)'<', (byte)'a', (byte)'>', 0xff],
                output,
                0,
                static (ref int _, in Element _, ref StartTagEditor _) => { }
            );
        }
        catch (DecoderFallbackException)
        {
            rejected = true;
        }

        await Assert.That(rejected).IsTrue();
        await Assert.That(output.WrittenCount).IsEqualTo(0);
    }

    private static ReadOnlySpan<byte> Get(in Element element, string name)
    {
        if (!element.TryGetAttribute(name, out var value))
            throw new InvalidOperationException($"Missing projected attribute '{name}'.");
        return value;
    }

    private sealed class QueryState
    {
        public List<string> Events { get; } = [];
        public StringBuilder Text { get; } = new();
    }
}
#endif
