#if NET10_0
using System.Runtime.CompilerServices;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.ReadOnlyDom;
using AngleSharp.ReadOnlyDom.Compact;
using AngleSharp.ReadOnlyDom.Compact.Document;
using AngleSharp.ReadOnlyDom.Compact.Parsing;
using AngleSharp.ReadOnlyDom.Compact.Projection;
using AngleSharp.ReadOnlyDom.Compact.Query;
using AngleSharp.ReadOnlyDom.Html;
using Node = AngleSharp.ReadOnlyDom.Compact.Query.Node;

namespace AngleSharp.Readonly.Tests;

internal sealed class CompactParserTests
{
    [Test]
    [Arguments(CompactDocumentLayout.FrozenColumns)]
    [Arguments(CompactDocumentLayout.Packed)]
    public async Task MeaningfulWhitespaceAfterInlineContentIsPreserved(CompactDocumentLayout layout)
    {
        const string html = "<article id=content><p>one <span>two</span> </p><p>three</p></article>";
        using var document = CompactParser.CreateParser(layout: layout).ParseCompactDocument(html);

        var content = document.Elements("article").WithAttribute("id", "content").First();

        await Assert.That(content.Text()).IsEqualTo("one two three");
    }

    [Test]
    [Arguments(CompactDocumentLayout.FrozenColumns)]
    [Arguments(CompactDocumentLayout.Packed)]
    public async Task StructuralWhitespaceBetweenBlockSiblingsRemainsDiscarded(CompactDocumentLayout layout)
    {
        const string html = "<body>\n  <div>one</div>\n  <div>two</div>\n</body>";
        using var document = CompactParser.CreateParser(layout: layout).ParseCompactDocument(html);

        var body = document.Elements("body").First();
        var childCount = 0;
        foreach (var _ in body.Children())
            childCount++;

        await Assert.That(childCount).IsEqualTo(2);
    }

    [Test]
    public async Task ByteMemoryInputConstructsCompactDocument()
    {
        var source = Encoding.UTF8.GetBytes("<main data-kind=sample>café</main>");

        using var document = CompactParser.CreateParser().ParseCompactDocument(source.AsMemory());
        var main = document.Elements("main").First();

        await Assert.That(main.Attr("data-kind").ToString()).IsEqualTo("sample");
        await Assert.That(main.Text()).IsEqualTo("café");
    }

    [Test]
    [Arguments(CompactDocumentLayout.FrozenColumns)]
    [Arguments(CompactDocumentLayout.Packed)]
    public async Task ResolvedNameIdsCanBeReusedAcrossQueriesAndAttributeReads(CompactDocumentLayout layout)
    {
        using var document = CompactParser
            .CreateParser(layout: layout)
            .ParseCompactDocument("<main class='selected wide' data-kind=sample>text</main>");
        var mainName = document.Name("main");
        var className = document.Name("class");
        var dataKind = document.Name("data-kind");

        var main = document.Elements(mainName).WithClass(className, "wide").WithAttribute(dataKind, "sample").First();

        await Assert.That(main.Exists).IsTrue();
        await Assert.That(main.HasClass(className, "selected")).IsTrue();
        await Assert.That(main.HasAttr(dataKind)).IsTrue();
        await Assert.That(main.Attr(dataKind).ToString()).IsEqualTo("sample");
    }

    [Test]
    [Arguments(CompactDocumentLayout.FrozenColumns)]
    [Arguments(CompactDocumentLayout.Packed)]
    public async Task StringNameQueriesDoNotRequireResolvedIds(CompactDocumentLayout layout)
    {
        using var document = CompactParser
            .CreateParser(layout: layout)
            .ParseCompactDocument("<main class='selected wide' data-kind=sample>text</main>");

        var main = document.Elements("main").WithClass("wide").WithAttribute("data-kind", "sample").First();
        var firstMain = document.IndexOfName("main");
        var mainCount = document.CountElements("main".AsSpan());

        await Assert.That(main.Exists).IsTrue();
        await Assert.That(main.HasClass("selected")).IsTrue();
        await Assert.That(main.HasAttr("data-kind")).IsTrue();
        await Assert.That(main.Attr("data-kind").ToString()).IsEqualTo("sample");
        await Assert.That(firstMain).IsEqualTo(main.Handle);
        await Assert.That(mainCount).IsEqualTo(1);
        await Assert.That(document.IndexOfName("missing")).IsEqualTo(-1);
        await Assert.That(document.CountElements("missing")).IsEqualTo(0);
    }

    [Test]
    [Arguments(CompactDocumentLayout.FrozenColumns)]
    [Arguments(CompactDocumentLayout.Packed)]
    public async Task ClassQueriesUseAllFiveHtmlWhitespaceSeparators(CompactDocumentLayout layout)
    {
        foreach (var separator in new[] { '\t', '\n', '\f', '\r', ' ' })
        {
            using var document = CompactParser
                .CreateParser(layout: layout)
                .ParseCompactDocument($"<p class='alpha{separator}beta'>value</p>");
            var paragraph = document.Elements("p").First();

            await Assert.That(paragraph.HasClass("alpha")).IsTrue();
            await Assert.That(paragraph.HasClass("beta")).IsTrue();
            await Assert.That(document.Elements("p").WithClass("beta").First().Exists).IsTrue();
        }
    }

    [Test]
    [Arguments(CompactDocumentLayout.FrozenColumns)]
    [Arguments(CompactDocumentLayout.Packed)]
    public async Task ClassQueriesDoNotTreatNbspAsHtmlWhitespace(CompactDocumentLayout layout)
    {
        const string joinedToken = "alpha\u00a0beta";
        using var document = CompactParser
            .CreateParser(layout: layout)
            .ParseCompactDocument($"<p class='{joinedToken}'>value</p>");
        var paragraph = document.Elements("p").First();

        await Assert.That(paragraph.HasClass("alpha")).IsFalse();
        await Assert.That(paragraph.HasClass("beta")).IsFalse();
        await Assert.That(paragraph.HasClass(joinedToken)).IsTrue();
        await Assert.That(document.Elements("p").WithClass("alpha").First().Exists).IsFalse();
        await Assert.That(document.Elements("p").WithClass(joinedToken).First().Exists).IsTrue();
    }

    [Test]
    public async Task ClassQueriesRejectEmptyAndCompositeTokens()
    {
        using var document = CompactParser.CreateParser().ParseCompactDocument("<p class='alpha beta'>value</p>");
        var paragraph = document.Elements("p").First();

        await Assert.That(() => paragraph.HasClass((string)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => document.Elements("p").WithClass(null!)).Throws<ArgumentNullException>();
        foreach (var token in new[] { "", "two tokens", "two\ttokens", "two\ntokens", "two\ftokens", "two\rtokens" })
        {
            await Assert.That(() => paragraph.HasClass(token)).Throws<ArgumentException>();
            await Assert.That(() => document.Elements("p").WithClass(token)).Throws<ArgumentException>();
        }
    }

    [Test]
    [Arguments(CompactDocumentLayout.FrozenColumns)]
    [Arguments(CompactDocumentLayout.Packed)]
    public async Task StructuralFormFeedBetweenBlockSiblingsIsDiscarded(CompactDocumentLayout layout)
    {
        using var document = CompactParser
            .CreateParser(layout: layout)
            .ParseCompactDocument("<body>\f<div>one</div>\f<div>two</div>\f</body>");
        var body = document.Elements("body").First();
        var childCount = 0;

        foreach (var child in body.Children())
        {
            await Assert.That(child.Is("div")).IsTrue();
            childCount++;
        }

        await Assert.That(childCount).IsEqualTo(2);
    }

    [Test]
    public async Task Utf8StreamConstructsCompactDocumentThroughBoundedSource()
    {
        var source = Encoding.UTF8.GetBytes("<main data-kind=stream>café</main>");
        var parser = CompactParser.CreateParser();

        using var document = await parser.ParseCompactDocumentAsync(new MemoryStream(source));
        var main = document.Elements("main").First();

        await Assert.That(main.Attr("data-kind").ToString()).IsEqualTo("stream");
        await Assert.That(main.Text()).IsEqualTo("café");
    }

    [Test]
    public async Task CoreNodeIsExactlySixteenBytes() => await Assert.That(Unsafe.SizeOf<CompactNode>()).IsEqualTo(16);

    [Test]
    [Arguments(CompactDocumentLayout.FrozenColumns)]
    [Arguments(CompactDocumentLayout.Packed)]
    public async Task ValuesSurviveTokenizerBufferReuseAfterParsing(CompactDocumentLayout layout)
    {
        var source = new StringBuilder("<body>");
        for (var i = 0; i < 64; i++)
            source
                .Append("<div data-idx='item ")
                .Append(i)
                .Append(" &amp; more'>value ")
                .Append(i)
                .Append(" &lt;ok&gt;</div>");
        source.Append("</body>");
        var html = source.ToString();

        using var document = CompactParser.CreateParser(layout: layout).ParseCompactDocument(html);
        ScribbleSharedCharPool(html.Length);

        var idxName = document.Name("data-idx");
        var index = 0;
        foreach (var div in document.Elements("div"))
        {
            await Assert.That(div.Attr(idxName).ToString()).IsEqualTo($"item {index} & more");
            await Assert.That(div.Text()).IsEqualTo($"value {index} <ok>");
            index++;
        }
        await Assert.That(index).IsEqualTo(64);
    }

    [Test]
    [Arguments(CompactDocumentLayout.FrozenColumns)]
    [Arguments(CompactDocumentLayout.Packed)]
    public async Task SparselyRetainedTextSurvivesTokenizerBufferReuse(CompactDocumentLayout layout)
    {
        // Long whitespace-only runs pass through the tokenizer's shared buffer and are then
        // dropped, so almost none of the buffered text is retained. This drives the freeze onto
        // its dense-copy path while the previous test's dense retention keeps the bulk path covered.
        var source = new StringBuilder("<body><div>a</div>");
        for (var i = 0; i < 32; i++)
            source.Append("<div>").Append(new string(' ', 512)).Append("</div>");
        source.Append("<div>z</div></body>");
        var html = source.ToString();

        using var document = CompactParser.CreateParser(layout: layout).ParseCompactDocument(html);
        ScribbleSharedCharPool(html.Length);

        var texts = new List<string>();
        foreach (var div in document.Elements("div"))
        {
            var text = div.Text();
            if (text.Length != 0)
                texts.Add(text);
        }
        await Assert.That(texts).IsEquivalentTo(["a", "z"]);
    }

    /// <summary>
    /// Makes dangling slices visible: if the freeze left any value pointing into the tokenizer's
    /// returned buffer, re-renting similarly sized arrays and overwriting them corrupts that data
    /// before the assertions read it back.
    /// </summary>
    private static void ScribbleSharedCharPool(int sourceLength)
    {
        var rented = new List<char[]>();
        for (var i = 0; i < 8; i++)
        {
            var buffer = System.Buffers.ArrayPool<char>.Shared.Rent(Math.Max(sourceLength, 1));
            buffer.AsSpan().Fill('!');
            rented.Add(buffer);
        }
        foreach (var buffer in rented)
            System.Buffers.ArrayPool<char>.Shared.Return(buffer);
    }

    [Test]
    [Arguments(CompactDocumentLayout.FrozenColumns)]
    [Arguments(CompactDocumentLayout.Packed)]
    public async Task SubtreeBoundariesSupportBoundedScansAndDirectChildren(CompactDocumentLayout layout)
    {
        using var document = CompactParser
            .CreateParser(layout: layout)
            .ParseCompactDocument(
                "<main><section><p>one</p><div><p>two</p></div><template><p>detached</p></template></section><aside>tail</aside></main>"
            );

        for (var handle = 0; handle < document.NodeCount; handle++)
        {
            var end = document.GetNode(handle).SubtreeEndExclusive;
            await Assert.That(end).IsGreaterThan(handle);
            await Assert.That(end).IsLessThanOrEqualTo(document.NodeCount);
        }

        var main = document.Elements("main").First();
        var section = main.Elements("section").First();
        var nestedParagraph = section.Elements("p").First();
        var template = section.Elements("template").First();
        var templateParagraph = default(Node);
        foreach (var child in template.TemplateContent())
        {
            templateParagraph = child;
            break;
        }
        var aside = main.Elements("aside").First();
        var directChildren = new List<string>();
        foreach (var child in main.Children())
            directChildren.Add(child.Name);

        await Assert.That(section.Elements("p").Count()).IsEqualTo(2);
        await Assert.That(section.Elements("aside").Count()).IsEqualTo(0);
        await Assert.That(nestedParagraph.IsDescendantOf(section)).IsTrue();
        await Assert.That(templateParagraph.IsDescendantOf(template)).IsFalse();
        await Assert.That(templateParagraph.IsDescendantOf(section)).IsFalse();
        await Assert.That(aside.IsDescendantOf(section)).IsFalse();
        await Assert.That(directChildren).IsEquivalentTo(["section", "aside"]);
    }

    [Test]
    public async Task EofProjectionMultiStepPathExecutesRequiredFeatures()
    {
        const string html =
            "<main><section id=content class='selected wide' data-ready><article><a href='/item'> one <b>two</b> </a></article></section></main>";
        var plan = CompactProjection
            .First(
                CompactProjectionSelector
                    .Tag("section")
                    .WithId("content")
                    .WithClass("wide")
                    .WithAttribute("data-ready")
                    .Child("article")
                    .Descendant("a")
                    .WithAttribute("href", "/item")
            )
            .Field("href", CompactFieldProjection.SelfAttribute("href"), required: true)
            .Field("text", CompactFieldProjection.SelfNormalizedText(), required: true)
            .Compile();

        var result = plan.ExecuteWithDiagnostics(html);

        await Assert.That(result.Rows.Count).IsEqualTo(1);
        await Assert.That(result.Rows[0]["href"].ToString()).IsEqualTo("/item");
        await Assert.That(result.Rows[0]["text"].ToString()).IsEqualTo("one two");
        await Assert.That(result.Counters.RowsProduced).IsEqualTo(1);
        await Assert.That(result.Counters.AttributesInspected).IsGreaterThan(0);
        await Assert.That(plan.Requirements.InspectedAttributes).Contains("data-ready");
    }

    [Test]
    public async Task EofProjectionNormalExecutionDoesNotCollectDiagnostics()
    {
        var result = CompactProjection
            .First(CompactProjectionSelector.Tag("main"))
            .Field("text", CompactFieldProjection.SelfNormalizedText())
            .Compile()
            .Execute("<main>value</main>");

        await Assert.That(result.Rows[0]["text"].ToString()).IsEqualTo("value");
        await Assert.That(result.Counters).IsEqualTo(default(CompactProjectionCounters));
    }

    [Test]
    [Arguments("<div id=content><p class=item data-v=one>A<p class=item>B")]
    [Arguments("<table><div id=content><span class=item data-v=one>A</span></div></table>")]
    [Arguments("<div id=content><b><i><span class=item data-v=one>A</b>B</i></span>")]
    public async Task EofProjectionMultiStepPathMatchesAngleSharpOnMalformedMarkup(string html)
    {
        var plan = CompactProjection
            .ForEach(CompactProjectionSelector.Tag("div").WithId("content").Descendant("span").WithClass("item"))
            .Field("value", CompactFieldProjection.SelfAttribute("data-v"), required: true)
            .Field("text", CompactFieldProjection.SelfNormalizedText())
            .Compile();
        using var expected = new HtmlParser().ParseDocument(html);

        var expectedRows = expected
            .QuerySelectorAll("div#content span.item[data-v]")
            .Select(element => $"{element.GetAttribute("data-v")}|{NormalizeWhitespace(element.TextContent)}")
            .ToArray();
        var result = plan.Execute(html);
        var actualRows = result.Rows.Select(row => $"{row["value"]}|{row["text"]}").ToArray();

        await Assert.That(actualRows).IsEquivalentTo(expectedRows);
    }

    [Test]
    public async Task EofProjectionRequiredFieldsRejectOnlyMissingValues()
    {
        const string html = "<p data-v=''>empty</p><p>missing</p><p data-v=x>value</p>";
        var required = CompactProjection
            .ForEach(CompactProjectionSelector.Tag("p"))
            .Field("value", CompactFieldProjection.SelfAttribute("data-v"), required: true)
            .Compile()
            .ExecuteWithDiagnostics(html);
        var optional = CompactProjection
            .ForEach(CompactProjectionSelector.Tag("p"))
            .Field("value", CompactFieldProjection.SelfAttribute("data-v"))
            .Compile()
            .Execute(html);

        await Assert.That(required.Rows.Count).IsEqualTo(2);
        await Assert.That(required.Rows[0]["value"].Exists).IsTrue();
        await Assert.That(required.Rows[0]["value"].Span.Length).IsEqualTo(0);
        await Assert.That(required.Counters.RowsRejected).IsEqualTo(1);
        await Assert.That(optional.Rows.Count).IsEqualTo(3);
        await Assert.That(optional.Rows[1]["value"].Exists).IsFalse();
        await Assert.That(() => optional.Rows[0]["unknown"]).Throws<KeyNotFoundException>();
    }

    [Test]
    public async Task EofProjectionPreflightsRequiredFieldsBeforeOptionalText()
    {
        var result = CompactProjection
            .First(CompactProjectionSelector.Tag("article"))
            .Field("text", CompactFieldProjection.SelfNormalizedText())
            .Field("required", CompactFieldProjection.SelfAttribute("data-required"), required: true)
            .Compile()
            .ExecuteWithDiagnostics("<article>expensive <b>optional</b> text</article>");

        await Assert.That(result.Rows.Count).IsEqualTo(0);
        await Assert.That(result.Counters.RowsRejected).IsEqualTo(1);
        await Assert.That(result.Counters.NormalizedTextValuesProjected).IsEqualTo(0);
    }

    [Test]
    public async Task EofProjectionReusesEquivalentFieldSelectorTargets()
    {
        const int NoiseElements = 20;
        var html = new StringBuilder("<article>");
        for (var index = 0; index < NoiseElements; index++)
            html.Append("<div></div>");
        html.Append("<a class=target href=/item title=Item>text</a></article>");

        var result = CompactProjection
            .First(CompactProjectionSelector.Tag("article"))
            .Field(
                "href",
                CompactFieldProjection.FirstAttribute(CompactProjectionSelector.Tag("a").WithClass("target"), "href"),
                required: true
            )
            .Field(
                "title",
                CompactFieldProjection.FirstAttribute(CompactProjectionSelector.Tag("A").WithClass("target"), "title")
            )
            .Compile()
            .ExecuteWithDiagnostics(html.ToString());

        await Assert.That(result.Rows[0]["href"].ToString()).IsEqualTo("/item");
        await Assert.That(result.Rows[0]["title"].ToString()).IsEqualTo("Item");
        await Assert.That(result.Counters.CandidateNodes).IsLessThanOrEqualTo(NoiseElements + 10);
    }

    [Test]
    public async Task EofProjectionFirstProjectionsRejectNullSelectors()
    {
        await Assert.That(() => CompactFieldProjection.FirstNormalizedText(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => CompactFieldProjection.FirstAttribute(null!, "href")).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task EofProjectionClassTokensUseOnlyHtmlWhitespaceAsSeparators()
    {
        const string joinedToken = "alpha\u00a0beta";
        const string html = $"<p class='{joinedToken}'>joined</p><p class='beta'>separate</p>";
        var beta = CompactProjection
            .ForEach(CompactProjectionSelector.Tag("p").WithClass("beta"))
            .Field("text", CompactFieldProjection.SelfNormalizedText())
            .Compile()
            .Execute(html);
        var joined = CompactProjection
            .ForEach(CompactProjectionSelector.Tag("p").WithClass(joinedToken))
            .Field("text", CompactFieldProjection.SelfNormalizedText())
            .Compile()
            .Execute(html);

        await Assert.That(beta.Rows.Count).IsEqualTo(1);
        await Assert.That(beta.Rows[0]["text"].ToString()).IsEqualTo("separate");
        await Assert.That(joined.Rows.Count).IsEqualTo(1);
        await Assert.That(joined.Rows[0]["text"].ToString()).IsEqualTo("joined");
    }

    [Test]
    public async Task EofProjectionClassTokenRejectsEmbeddedHtmlWhitespace()
    {
        foreach (var token in new[] { "two tokens", "two\ttokens", "two\ntokens", "two\ftokens", "two\rtokens" })
            await Assert.That(() => CompactProjectionSelector.Tag("p").WithClass(token)).Throws<ArgumentException>();
    }

    [Test]
    public async Task EofProjectionTraversesDeepTreesWithoutRecursiveWalkers()
    {
        const int Depth = 12_000;
        var html = new StringBuilder(Depth * 22);
        html.Append("<body>");
        for (var index = 0; index < Depth; index++)
            html.Append("<div>");
        html.Append("<article data-result=ok>");
        for (var index = 0; index < Depth; index++)
            html.Append("<section>");
        html.Append("<span> deep   value </span>");
        for (var index = 0; index < Depth; index++)
            html.Append("</section>");
        html.Append("</article>");
        for (var index = 0; index < Depth; index++)
            html.Append("</div>");
        html.Append("</body>");

        var result = CompactProjection
            .First(CompactProjectionSelector.Tag("article"))
            .Field(
                "target",
                CompactFieldProjection.FirstNormalizedText(CompactProjectionSelector.Tag("span")),
                required: true
            )
            .Field("text", CompactFieldProjection.SelfNormalizedText(), required: true)
            .Compile()
            .Execute(html.ToString());

        await Assert.That(result.Rows.Count).IsEqualTo(1);
        await Assert.That(result.Rows[0]["target"].ToString()).IsEqualTo("deep value");
        await Assert.That(result.Rows[0]["text"].ToString()).IsEqualTo("deep value");
    }

    [Test]
    [Arguments("<div id=content> one <b>two</b> &amp; three</div>")]
    [Arguments("<div id=content><b><i>one</b> two</i><p>three")]
    [Arguments("<table><div id=content>before<span>inside</span></div><tr><td>cell</table>")]
    [Arguments("<div id=content><table>before<tr><td>cell</td></tr>after</table>end")]
    [Arguments("<template><div id=content>wrong</div></template><div id=content>right &amp; final</div>")]
    [Arguments("<svg><foreignObject><div id=content>svg <b>html</b></div></foreignObject></svg>")]
    [Arguments("<math><annotation-xml encoding='text/html'><div id=content>math</div></annotation-xml></math>")]
    [Arguments("<b class=before>before<div id=content>inside</b> after</div>")]
    public async Task EofProjectionNormalizedTextMatchesConstructedDom(string html)
    {
        using var expectedDocument = new HtmlParser().ParseDocument(html);
        var expected = expectedDocument.QuerySelector("div#content");
        var plan = CompactProjection
            .First(CompactProjectionSelector.Tag("div").WithId("content"))
            .Field("text", CompactFieldProjection.SelfNormalizedText())
            .Compile();

        var actual = plan.ExecuteWithDiagnostics(html);

        await Assert.That(actual.Rows.Count).IsEqualTo(expected is null ? 0 : 1);
        if (expected is not null)
        {
            var value = actual.Rows[0]["text"];
            await Assert.That(value.ToString()).IsEqualTo(NormalizeWhitespace(expected.TextContent));
        }
        await Assert.That(actual.Counters.TokensProcessed).IsGreaterThan(0);
        await Assert.That(actual.Counters.NodesMaterialized).IsGreaterThan(0);
        await Assert.That(actual.Counters.AttributesInspected).IsGreaterThan(0);
        await Assert.That(actual.Counters.InputBytesConsumed).IsEqualTo(Encoding.UTF8.GetByteCount(html));
    }

    [Test]
    public async Task EofProjectionFiltersUnneededAttributesAndReportsDecodedValues()
    {
        const string Html = "<main data-a=1 data-b=2><div id=content data-c=3>one &amp; two</div></main>";
        var plan = CompactProjection
            .First(CompactProjectionSelector.Tag("div").WithId("content"))
            .Field("text", CompactFieldProjection.SelfNormalizedText())
            .Compile();

        var result = plan.ExecuteWithDiagnostics(Html);

        await Assert.That(result.Rows[0]["text"].ToString()).IsEqualTo("one & two");
        await Assert.That(result.Counters.AttributesRetained).IsEqualTo(1);
        await Assert.That(result.Counters.ValuesDecoded).IsGreaterThan(0);
    }

    [Test]
    public async Task EofProjectionProjectsArticleAsJsonText()
    {
        const string Html = """
            <article id="content">
              <h1>Parsing <em>real</em> HTML</h1>
              <p>Use the <a href="/parser">HTML parser</a>, not regex.</p>
              <ul><li>Correct tables</li><li>Malformed markup</li></ul>
            </article>
            """;
        var article = CompactProjectionSelector.Tag("article").WithId("content");
        var plan = CompactProjection
            .First(article)
            .Field("title", CompactFieldProjection.FirstNormalizedText(CompactProjectionSelector.Tag("h1")))
            .Field("text", CompactFieldProjection.SelfNormalizedText())
            .Compile();

        var result = plan.ExecuteWithDiagnostics(Html);

        await Assert.That(result.Rows.Count).IsEqualTo(1);
        await Assert.That(result.Rows[0]["title"].ToString()).IsEqualTo("Parsing real HTML");
        await Assert
            .That(result.Rows[0]["text"].ToString())
            .IsEqualTo("Parsing real HTML Use the HTML parser, not regex. Correct tablesMalformed markup");
        await Assert.That(result.Counters.InputBytesConsumed).IsEqualTo(Encoding.UTF8.GetByteCount(Html));
        await Assert.That(result.Counters.RowsProduced).IsEqualTo(1);
    }

    [Test]
    public async Task EofProjectionProjectsRepeatedSearchResultObjects()
    {
        const string Html = """
            <main>
              <article class="result"><h2><a href="/a">Arena parsing</a></h2><p class="snippet">Build only what you need.</p></article>
              <article class="result"><h2><a href="/b">Compact DOM</a></h2><p class="snippet">Retain a reusable tree.</p></article>
            </main>
            """;
        var plan = CompactProjection
            .ForEach(CompactProjectionSelector.Tag("article").WithClass("result"))
            .Field("title", CompactFieldProjection.FirstNormalizedText(CompactProjectionSelector.Tag("h2")))
            .Field(
                "url",
                CompactFieldProjection.FirstAttribute(CompactProjectionSelector.Tag("a"), "href"),
                required: true
            )
            .Field(
                "snippet",
                CompactFieldProjection.FirstNormalizedText(CompactProjectionSelector.Tag("p").WithClass("snippet"))
            )
            .Compile();

        var result = plan.ExecuteWithDiagnostics(Html);

        await Assert.That(result.Rows.Count).IsEqualTo(2);
        await Assert.That(result.Rows[0]["title"].ToString()).IsEqualTo("Arena parsing");
        await Assert.That(result.Rows[0]["url"].ToString()).IsEqualTo("/a");
        await Assert.That(result.Rows[1]["snippet"].ToString()).IsEqualTo("Retain a reusable tree.");
        await Assert.That(result.Counters.RowsRejected).IsEqualTo(0);
    }

    [Test]
    public async Task EofProjectionRequiredAttributeDistinguishesEmptyFromMissing()
    {
        const string Html =
            "<article class=result><a href=''>empty</a></article><article class=result><a>missing</a></article>";
        var plan = CompactProjection
            .ForEach(CompactProjectionSelector.Tag("article").WithClass("result"))
            .Field(
                "url",
                CompactFieldProjection.FirstAttribute(CompactProjectionSelector.Tag("a"), "href"),
                required: true
            )
            .Compile();

        var result = plan.ExecuteWithDiagnostics(Html);

        await Assert.That(result.Rows.Count).IsEqualTo(1);
        await Assert.That(result.Rows[0]["url"].Exists).IsTrue();
        await Assert.That(result.Rows[0]["url"].Span.Length).IsEqualTo(0);
        await Assert.That(result.Counters.RowsRejected).IsEqualTo(1);
    }

    [Test]
    public async Task EofProjectionAttributeOnlyPlanDoesNotRetainTextPayloads()
    {
        const string Html =
            "<article class=result>large &amp; irrelevant text<a href=/item>also irrelevant</a></article>";
        var result = CompactProjection
            .First(CompactProjectionSelector.Tag("article").WithClass("result"))
            .Field(
                "url",
                CompactFieldProjection.FirstAttribute(CompactProjectionSelector.Tag("a"), "href"),
                required: true
            )
            .Compile()
            .ExecuteWithDiagnostics(Html);

        await Assert.That(result.Rows[0]["url"].ToString()).IsEqualTo("/item");
        await Assert.That(result.Counters.TextValuesRetained).IsEqualTo(0);
    }

    [Test]
    [Arguments("<article class=result><b><i>one</b> two</i><p>three")]
    [Arguments("<table><article class=result>before<span>inside</span></article><tr><td>cell</table>")]
    public async Task EofProjectionNormalizedTextMatchesFinalAngleSharpTopology(string html)
    {
        using var expectedDocument = new HtmlParser().ParseDocument(html);
        var expected = expectedDocument.QuerySelector("article.result");
        var plan = CompactProjection
            .First(CompactProjectionSelector.Tag("article").WithClass("result"))
            .Field("text", CompactFieldProjection.SelfNormalizedText())
            .Compile();

        var actual = plan.Execute(html);

        await Assert.That(actual.Rows.Count).IsEqualTo(expected is null ? 0 : 1);
        if (expected is not null)
            await Assert.That(actual.Rows[0]["text"].ToString()).IsEqualTo(NormalizeWhitespace(expected.TextContent));
    }

    [Test]
    public async Task EofProjectionReportsMissingTarget()
    {
        var plan = CompactProjection
            .First(CompactProjectionSelector.Tag("div").WithId("content"))
            .Field("text", CompactFieldProjection.SelfNormalizedText())
            .Compile();

        var result = plan.Execute("<main><p>none</p></main>");

        await Assert.That(result.Rows.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EofProjectionChildAxisRejectsDeeperNesting()
    {
        var plan = CompactProjection
            .ForEach(CompactProjectionSelector.Tag("ul").Child("li"))
            .Field("text", CompactFieldProjection.SelfNormalizedText())
            .Compile();

        var result = plan.Execute("<ul><li>direct</li><li><ul><li>nested</li></ul></li></ul>");

        await Assert.That(result.Rows.Count).IsEqualTo(3);
        await Assert.That(result.Rows[0]["text"].ToString()).IsEqualTo("direct");
        await Assert.That(result.Rows[2]["text"].ToString()).IsEqualTo("nested");
    }

    [Test]
    public async Task EofProjectionChildAxisDoesNotMatchAcrossAnIntermediateElement()
    {
        var plan = CompactProjection
            .ForEach(CompactProjectionSelector.Tag("main").Child("p"))
            .Field("text", CompactFieldProjection.SelfNormalizedText())
            .Compile();

        var result = plan.Execute("<main><p>direct</p><div><p>indirect</p></div></main>");

        await Assert.That(result.Rows.Count).IsEqualTo(1);
        await Assert.That(result.Rows[0]["text"].ToString()).IsEqualTo("direct");
    }

    [Test]
    public async Task EofProjectionDescendantAxisMatchesAtAnyDepth()
    {
        var plan = CompactProjection
            .First(CompactProjectionSelector.Tag("main").Descendant("span").WithClass("target"))
            .Field("text", CompactFieldProjection.SelfNormalizedText())
            .Compile();

        var result = plan.Execute("<main><div><section><span class=target>deep</span></section></div></main>");

        await Assert.That(result.Rows.Count).IsEqualTo(1);
        await Assert.That(result.Rows[0]["text"].ToString()).IsEqualTo("deep");
    }

    [Test]
    public async Task EofProjectionDescendantChainBacktracksPastANonMatchingAncestor()
    {
        // The nearest `section` ancestor of the `p` has no matching `article` above it; matching must
        // retry the outer `section` rather than giving up on the first candidate.
        var plan = CompactProjection
            .First(CompactProjectionSelector.Tag("article").Descendant("section").Descendant("p"))
            .Field("text", CompactFieldProjection.SelfNormalizedText())
            .Compile();

        var result = plan.Execute("<article><section><div><p>wanted</p></div></section></article>");

        await Assert.That(result.Rows.Count).IsEqualTo(1);
        await Assert.That(result.Rows[0]["text"].ToString()).IsEqualTo("wanted");
    }

    [Test]
    public async Task EofProjectionDescendantBacktrackingMemoizesRepeatedStates()
    {
        const int Depth = 30;
        const int RepeatedSteps = 11;
        var selector = CompactProjectionSelector.Tag("main");
        for (var i = 0; i < RepeatedSteps; i++)
            selector = selector.Descendant("div").WithAttribute("data-step");
        selector = selector.Descendant("p");

        var plan = CompactProjection
            .First(selector)
            .Field("text", CompactFieldProjection.SelfNormalizedText())
            .Compile();
        var html = new StringBuilder("<section>");
        for (var i = 0; i < Depth; i++)
            html.Append("<div data-step>");
        html.Append("<p>no matching main ancestor</p>");
        for (var i = 0; i < Depth; i++)
            html.Append("</div>");
        html.Append("</section>");

        var result = plan.ExecuteWithDiagnostics(html.ToString());

        await Assert.That(result.Rows.Count).IsEqualTo(0);
        await Assert.That(result.Counters.AttributesInspected).IsLessThanOrEqualTo(Depth * RepeatedSteps);
    }

    [Test]
    public async Task EofProjectionPathStepsContributeTheirAttributesToRequirements()
    {
        var plan = CompactProjection
            .First(CompactProjectionSelector.Tag("main").WithAttribute("data-scope").Child("p").WithId("lead"))
            .Field("text", CompactFieldProjection.SelfNormalizedText())
            .Compile();

        await Assert.That(plan.Requirements.InspectedAttributes).Contains("data-scope");
        await Assert.That(plan.Requirements.InspectedAttributes).Contains("id");

        var result = plan.Execute("<main data-scope=a><p id=lead>hit</p></main>");

        await Assert.That(result.Rows.Count).IsEqualTo(1);
        await Assert.That(result.Rows[0]["text"].ToString()).IsEqualTo("hit");
    }

    [Test]
    public async Task AppendOnlyDocumentsFreezeColumnsByDefault()
    {
        using var document = CompactParser.CreateParser().ParseCompactDocument("<main><p>x</p></main>");

        await Assert.That(document.Layout).IsEqualTo(CompactDocumentLayout.FrozenColumns);
    }

    [Test]
    public async Task ParsersNotBuiltByCreateParserAreRejected()
    {
        var parser = new HtmlParser();

        await Assert
            .That(() => parser.ParseCompactDocument("<main><p>x</p></main>"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task PackedLayoutRemainsExplicitlyAvailable()
    {
        using var document = CompactParser
            .CreateParser(layout: CompactDocumentLayout.Packed)
            .ParseCompactDocument("<main><p>x</p></main>");

        await Assert.That(document.Layout).IsEqualTo(CompactDocumentLayout.Packed);
    }

    [Test]
    [Arguments(CompactDocumentLayout.FrozenColumns)]
    [Arguments(CompactDocumentLayout.Packed)]
    public async Task ElementQueriesExcludeNonElementNames(CompactDocumentLayout layout)
    {
        using var document = CompactParser.CreateParser(layout: layout).ParseCompactDocument("<main>text</main>");

        await Assert.That(document.Elements("#text").Count()).IsEqualTo(0);
    }

    [Test]
    public async Task MutationHeavyMarkupFallsBackToPackedLayout()
    {
        using var document = CompactParser
            .CreateParser()
            .ParseCompactDocument("<main><table>before<tr><td>x</td></tr></table></main>");

        await Assert.That(document.Layout).IsEqualTo(CompactDocumentLayout.Packed);
    }

    [Test]
    public async Task InlineReferenceListLayoutsAreExplicit()
    {
        await Assert.That(Unsafe.SizeOf<SmallReferenceList2<object>>()).IsEqualTo(32);
    }

    [Test]
    public async Task DirectArenaMatchesReadOnlyFactoryOnWildMarkup()
    {
        string[] corpus =
        [
            "<p>one<div>two</p>three",
            "<!doctype html><!--a--><html><head><title>x</title><body><ul><li>a<li>b</ul>",
            "<b><i>one</b>two</i>",
            "<b class='same'><b class='same'><b class='same'><b class='same'>x</b></b></b></b>",
            "<template><section data-x='1'>inside</section></template><main>outside</main>",
            "text &amp; &#x1f600; <!-- broken",
        ];

        foreach (var html in corpus)
        {
            using var expected = new HtmlParser(
                new HtmlParserOptions { SkipComments = true, SkipProcessingInstructions = true }
            ).ParseDocument(html);
            using var actual = CompactParser.CreateParser().ParseCompactDocument(html);
            await Assert.That(Snapshot(actual, 0)).IsEqualTo(Snapshot(expected));
        }
    }

    [Test]
    [Arguments(CompactDocumentLayout.FrozenColumns)]
    [Arguments(CompactDocumentLayout.Packed)]
    public async Task DescendantTextExcludesTemplateContentsLikeAngleSharp(CompactDocumentLayout layout)
    {
        const string html =
            "<div id=content><b>one<i> &amp; two</b> three</i><template><p>template text</p></template></div>";
        using var expected = new HtmlParser().ParseDocument(html);
        using var actual = CompactParser.CreateParser(layout: layout).ParseCompactDocument(html);
        var actualContent = actual.Elements("div").WithAttribute("id", "content").First();
        var actualTemplate = actual.Elements("template").First();
        var expectedContent = expected.QuerySelector("#content")!.TextContent;
        var destination = new char[actualContent.TextLength()];

        await Assert.That(actualContent.Text()).IsEqualTo(expectedContent);
        await Assert.That(actualContent.TryWriteText(destination, out var written)).IsTrue();
        await Assert.That(new string(destination, 0, written)).IsEqualTo(expectedContent);
        await Assert.That(actualTemplate.Text()).IsEqualTo(expected.QuerySelector("template")!.TextContent);
        await Assert.That(actualTemplate.TextLength()).IsEqualTo(0);
        await Assert.That(actualTemplate.TryWriteText([], out var templateWritten)).IsTrue();
        await Assert.That(templateWritten).IsEqualTo(0);
    }

    [Test]
    [Arguments(CompactDocumentLayout.FrozenColumns)]
    [Arguments(CompactDocumentLayout.Packed)]
    public async Task TemplateContentHasASeparateTraversalBoundary(CompactDocumentLayout layout)
    {
        using var document = CompactParser
            .CreateParser(layout: layout)
            .ParseCompactDocument("<template><section><p>inside</p></section></template><main>outside</main>");
        var template = document.Elements("template").First();
        var childCount = 0;
        foreach (var _ in template.Children())
            childCount++;
        var contentCount = 0;
        foreach (var _ in template.TemplateContent())
            contentCount++;

        await Assert.That(childCount).IsEqualTo(0);
        await Assert.That(contentCount).IsEqualTo(1);
        await Assert.That(document.Elements("section").Count()).IsEqualTo(0);
        await Assert.That(document.Elements("p").Count()).IsEqualTo(0);
        await Assert.That(document.Elements("main").Count()).IsEqualTo(1);
        await Assert.That(document.CountElements(document.Name("p"))).IsEqualTo(0);
    }

    [Test]
    public async Task ForeignElementNamedTemplateKeepsOrdinaryChildren()
    {
        using var document = CompactParser
            .CreateParser()
            .ParseCompactDocument("<svg><template><circle></circle></template></svg>");
        var template = document.Elements("template").First();
        var childCount = 0;
        foreach (var _ in template.Children())
            childCount++;
        var contentCount = 0;
        foreach (var _ in template.TemplateContent())
            contentCount++;

        await Assert.That(childCount).IsEqualTo(1);
        await Assert.That(contentCount).IsEqualTo(0);
        await Assert.That(document.Elements("circle").Count()).IsEqualTo(1);
    }

    [Test]
    [Arguments("<svg><foreignObject><div>x</div></foreignObject></svg>")]
    [Arguments("<svg><desc><div>x</div></desc></svg>")]
    [Arguments("<svg><title><div>x</div></title></svg>")]
    [Arguments("<math><mi><form><form><input>")]
    [Arguments("<math><mn><div>x</div></mn></math>")]
    [Arguments("<math><mo><div>x</div></mo></math>")]
    [Arguments("<math><ms><div>x</div></ms></math>")]
    [Arguments("<math><mtext><div>x</div></mtext></math>")]
    [Arguments("<math><annotation-xml encoding=text/html><div>x</div></annotation-xml></math>")]
    public async Task FactoryPathMatchesForeignElementIntegrationPoint(string html)
    {
        using var expected = new HtmlParser().ParseDocument(html);
        using var actual = CompactParser.CreateParser().ParseCompactDocument(html);

        await Assert.That(Snapshot(actual, 0)).IsEqualTo(Snapshot(expected));
    }

    [Test]
    public async Task MetadataColumnsRemainOptional()
    {
        using var minimal = CompactParser.CreateParser().ParseCompactDocument("<main><p>x</p></main>");
        using var navigable = CompactParser
            .CreateParser(CompactMetadataOptions.ParentLinks | CompactMetadataOptions.SourceLocations)
            .ParseCompactDocument("<main><p>x</p></main>");
        await Assert.That(minimal.HasParentLinks).IsFalse();
        await Assert.That(minimal.HasSourceLocations).IsFalse();
        await Assert.That(navigable.HasParentLinks).IsTrue();
        await Assert.That(navigable.HasSourceLocations).IsTrue();
        await Assert.That(navigable.GetParent(1)).IsEqualTo(0);
        await Assert.That(minimal.Elements("main").First().TryGetSourceLocation(out _)).IsFalse();
        await Assert.That(navigable.Elements("main").First().TryGetSourceLocation(out var source)).IsTrue();
        await Assert.That(source.Index).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    [Arguments(CompactDocumentLayout.FrozenColumns)]
    [Arguments(CompactDocumentLayout.Packed)]
    public async Task DisposedDocumentRejectsPublicReadsAndDisposeRemainsIdempotent(CompactDocumentLayout layout)
    {
        var document = CompactParser
            .CreateParser(
                CompactMetadataOptions.ParentLinks | CompactMetadataOptions.SourceLocations,
                layout: layout
            )
            .ParseCompactDocument("<main id=content class='selected'><p>x</p></main>");
        var root = document.Root();
        var main = document.Elements("main").First();
        var children = main.Children();
        var descendants = document.Descendants();
        var elements = document.Elements("p");
        var elementEnumerator = elements.GetEnumerator();

        document.Dispose();
        document.Dispose();

        await Assert.That(() => document.NodeCount).Throws<ObjectDisposedException>();
        await Assert.That(() => document.AttributeCount).Throws<ObjectDisposedException>();
        await Assert.That(() => document.HasParentLinks).Throws<ObjectDisposedException>();
        await Assert.That(() => document.HasSourceLocations).Throws<ObjectDisposedException>();
        await Assert.That(() => document.Root()).Throws<ObjectDisposedException>();
        await Assert.That(() => document.Descendants()).Throws<ObjectDisposedException>();
        await Assert.That(() => document.Elements("main")).Throws<ObjectDisposedException>();
        await Assert.That(() => main.Descendants()).Throws<ObjectDisposedException>();
        await Assert.That(() => main.Elements("p")).Throws<ObjectDisposedException>();

        await Assert.That(() => root.Exists).Throws<ObjectDisposedException>();
        await Assert.That(() => main.Kind).Throws<ObjectDisposedException>();
        await Assert.That(() => main.IsElement).Throws<ObjectDisposedException>();
        await Assert.That(() => main.Name).Throws<ObjectDisposedException>();
        await Assert.That(() => main.LocalName.ToString()).Throws<ObjectDisposedException>();
        await Assert.That(() => main.Is("main")).Throws<ObjectDisposedException>();
        await Assert.That(() => main.Parent).Throws<ObjectDisposedException>();
        await Assert.That(() => main.IsDescendantOf(root)).Throws<ObjectDisposedException>();
        await Assert.That(() => main.TryGetSourceLocation(out _)).Throws<ObjectDisposedException>();
        await Assert.That(() => main.Attr("id").ToString()).Throws<ObjectDisposedException>();
        await Assert.That(() => main.HasAttr("id")).Throws<ObjectDisposedException>();
        await Assert.That(() => main.HasClass("selected")).Throws<ObjectDisposedException>();
        await Assert.That(() => main.Text()).Throws<ObjectDisposedException>();
        await Assert.That(() => main.TextLength()).Throws<ObjectDisposedException>();
        await Assert.That(() => main.AppendText(new StringBuilder())).Throws<ObjectDisposedException>();
        await Assert.That(() => main.WriteText(TextWriter.Null)).Throws<ObjectDisposedException>();
        await Assert
            .That(() => main.WriteText(new System.Buffers.ArrayBufferWriter<char>()))
            .Throws<ObjectDisposedException>();
        await Assert.That(() => main.TryWriteText(new char[8], out _)).Throws<ObjectDisposedException>();
        await Assert.That(() => main.Children()).Throws<ObjectDisposedException>();
        await Assert.That(() => main.TemplateContent()).Throws<ObjectDisposedException>();

        await Assert.That(() => children.Current).Throws<ObjectDisposedException>();
        await Assert.That(() => children.MoveNext()).Throws<ObjectDisposedException>();
        await Assert.That(() => children.GetEnumerator()).Throws<ObjectDisposedException>();
        await Assert.That(() => descendants.Current).Throws<ObjectDisposedException>();
        await Assert.That(() => descendants.MoveNext()).Throws<ObjectDisposedException>();
        await Assert.That(() => descendants.GetEnumerator()).Throws<ObjectDisposedException>();
        await Assert.That(() => elements.WithClass("selected")).Throws<ObjectDisposedException>();
        await Assert.That(() => elements.WithAttribute("id")).Throws<ObjectDisposedException>();
        await Assert.That(() => elements.GetEnumerator()).Throws<ObjectDisposedException>();
        await Assert.That(() => elements.Count()).Throws<ObjectDisposedException>();
        await Assert.That(() => elements.First()).Throws<ObjectDisposedException>();
        await Assert.That(() => elementEnumerator.Current).Throws<ObjectDisposedException>();
        await Assert.That(() => elementEnumerator.MoveNext()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task ReusableParserCreatesIndependentDocuments()
    {
        var parser = CompactParser.CreateParser();
        using var first = parser.ParseCompactDocument("<main><p>first</p></main>");
        var firstCount = first.NodeCount;
        first.Dispose();

        using var second = parser.ParseCompactDocument("<main><p>second</p><p>third</p></main>");
        await Assert.That(second.NodeCount).IsGreaterThan(firstCount);
        await Assert.That(second.FindNameId("main")).IsNotEqualTo(ushort.MaxValue);
    }

    [Test]
    public async Task FrozenDocumentOwnsValuesAfterTokenizerBuffersAreReused()
    {
        var expectedClass = $"stable-{new string('a', 512)}";
        using var first = CompactParser
            .CreateParser()
            .ParseCompactDocument($"<main class='{expectedClass}'>stable text</main>");

        for (var i = 0; i < 32; i++)
        {
            using var other = CompactParser
                .CreateParser()
                .ParseCompactDocument($"<main class='replacement-{i}-{new string('z', 512)}'>other</main>");
        }

        var main = first.Elements("main").First();
        await Assert.That(main.Attr("class").ToString()).IsEqualTo(expectedClass);
        await Assert.That(main.Text()).IsEqualTo("stable text");
    }

    [Test]
    public async Task KnownAndCustomNamesShareStableDocumentIds()
    {
        using var document = CompactParser
            .CreateParser()
            .ParseCompactDocument("<main><x-widget data-custom='x'></x-widget></main>");

        var main = document.FindNameId("main");
        var customElement = document.FindNameId("x-widget");
        var customAttribute = document.FindNameId("data-custom");
        await Assert.That(main).IsNotEqualTo(ushort.MaxValue);
        await Assert.That(customElement).IsNotEqualTo(ushort.MaxValue);
        await Assert.That(customAttribute).IsNotEqualTo(ushort.MaxValue);
        await Assert.That(document.GetName(main)).IsEqualTo("main");
        await Assert.That(document.GetName(customElement)).IsEqualTo("x-widget");
        await Assert.That(document.GetName(customAttribute)).IsEqualTo("data-custom");
        await Assert.That(document.FindNameId("aside")).IsEqualTo(ushort.MaxValue);
    }

    [Test]
    public async Task AttributeFilteringMatchesAngleSharpAcrossDiverseShapes()
    {
        string[] corpus =
        [
            "<main><p>attribute free</p></main>",
            "<article id='a'><a class='link' href='/x'>x</a></article>",
            "<form action='/save' method='post'><input id='x' class='field' name='x' value='1' required></form>",
            "<table data-table='x'>text<tr><td id='cell' colspan='2'>x</td></tr></table>",
            "<template data-template='x'><b id='b'><i class='i'>one</b>two</i></template>",
        ];

        foreach (var html in corpus)
        {
            var expectedParser = new HtmlParser(
                new HtmlParserOptions
                {
                    SkipComments = true,
                    SkipProcessingInstructions = true,
                    ShouldEmitAttribute = static (ref StructHtmlToken _, ReadOnlyMemory<char> _) => false,
                }
            );
            using var expected = expectedParser.ParseDocument(html);
            using var actual = CompactParser
                .CreateParser(attributeFilter: static (ref StructHtmlToken _, ReadOnlyMemory<char> _) => false)
                .ParseCompactDocument(html);

            await Assert.That(actual.AttributeCount).IsEqualTo(0);
            await Assert.That(Snapshot(actual, 0)).IsEqualTo(Snapshot(expected));
        }
    }

    [Test]
    public async Task TinyCapacityHintsGrowIndependentPayloadAndAttributeArenas()
    {
        const string html =
            "<main id='m'><section class='a' data-x='1'><p title='p'>one</p><p title='q'>two</p></section></main>";
        var hints = new CompactParserHints
        {
            InitialNodeCapacity = 1,
            InitialPayloadCapacity = 1,
            InitialAttributeCapacity = 1,
        };

        using var expected = new HtmlParser(
            new HtmlParserOptions { SkipComments = true, SkipProcessingInstructions = true }
        ).ParseDocument(html);
        using var actual = CompactParser.CreateParser(hints: hints).ParseCompactDocument(html);

        await Assert.That(actual.AttributeCount).IsEqualTo(5);
        await Assert.That(Snapshot(actual, 0)).IsEqualTo(Snapshot(expected));
    }

    [Test]
    public async Task ProductionParserControlsFlowThroughDirectSession()
    {
        const string html =
            "<head><script>ignored()</script></head><body><div id='drop' class='keep'><p>x</p></div></body>";
        var parserOptions = new HtmlParserOptions
        {
            IsNotSupportingFrames = true,
            SkipScriptText = true,
            SkipComments = true,
            SkipProcessingInstructions = true,
            DisableElementPositionTracking = true,
            ShouldEmitAttribute = static (ref StructHtmlToken token, ReadOnlyMemory<char> name) =>
                token.Name == "div" && name.Span is "class",
        };
        var expectedFilter = new FirstTagAndAllChildren("body");
        var actualFilter = new FirstTagAndAllChildren("body");
        var expectedParser = new HtmlParser(parserOptions, ReadOnlyParser.DefaultContext);
        using var expected = expectedParser.ParseReadOnlyDocument(html.AsMemory(), expectedFilter.Loop);
        var parser = CompactParser.CreateParser(
            parserOptions: parserOptions,
            attributeFilter: static (ref StructHtmlToken token, ReadOnlyMemory<char> name) =>
                token.Name == "div" && name.Span is "class"
        );
        using var actual = parser.ParseCompactDocument(html.AsMemory(), actualFilter.Loop);

        await Assert.That(actual.AttributeCount).IsEqualTo(1);
        await Assert.That(Snapshot(actual, 0)).IsEqualTo(Snapshot(expected));
    }

    [Test]
    public async Task RequestedLengthInputDoesNotParseUnusedTail()
    {
        const string retained = "<main><p>x</p></main>";
        var input = (retained + "<aside>unused</aside>").ToCharArray();
        using var expected = new HtmlParser(
            new HtmlParserOptions { SkipComments = true, SkipProcessingInstructions = true }
        ).ParseDocument(retained);
        using var actual = CompactParser.CreateParser().ParseCompactDocument(input, retained.Length);

        await Assert.That(Snapshot(actual, 0)).IsEqualTo(Snapshot(expected));
    }

    [Test]
    public async Task SubtreeMiddlewareUsesHtmlVoidAndSelfClosingSemantics()
    {
        const string html =
            "<body><section id='target'><img><input><div/>inside</div><p>x</p></section><aside>tail</aside></body>";
        var expectedFilter = new OnlyElementWithIdAndDescendants("section", "target");
        var actualFilter = new OnlyElementWithIdAndDescendants("section", "target");
        var parserOptions = new HtmlParserOptions
        {
            SkipComments = true,
            SkipProcessingInstructions = true,
            ShouldEmitAttribute = static (ref StructHtmlToken _, ReadOnlyMemory<char> name) => name.Span is "id",
        };
        var expectedParser = new HtmlParser(parserOptions, ReadOnlyParser.DefaultContext);
        using var expected = expectedParser.ParseReadOnlyDocument(html, expectedFilter.Loop);
        var parser = CompactParser.CreateParser(parserOptions: parserOptions);
        using var actual = parser.ParseCompactDocument(html, actualFilter.Loop);

        await Assert.That(Snapshot(actual, 0)).IsEqualTo(Snapshot(expected));
        await Assert.That(actual.FindNameId("aside")).IsEqualTo(ushort.MaxValue);
    }

    private static string Snapshot(INode node)
    {
        var name = node is IElement namedElement ? namedElement.LocalName : node.NodeName;
        var result = $"{Kind(node)}:{name}[";
        if (node is IElement element)
            result += string.Join(",", element.Attributes.Select(attribute => $"{attribute.Name}={attribute.Value}"));
        result += "]{";
        var children = node is IHtmlTemplateElement template ? template.Content.ChildNodes : node.ChildNodes;
        foreach (var child in children)
        {
            if (child.NodeType is not (NodeType.Comment or NodeType.ProcessingInstruction))
                result += Snapshot(child);
        }
        return result + "}";
    }

    private static string Snapshot(IReadOnlyNode node)
    {
        var result = $"{Kind(node)}:{node.NodeName}[";
        if (node is IReadOnlyElement element)
            result += string.Join(",", element.Attributes.Select(attribute => $"{attribute.Name}={attribute.Value}"));
        result += "]{";
        var children = node is IReadOnlyTemplateElement template ? template.Content : node.ChildNodes;
        foreach (var child in children)
            result += Snapshot(child);
        return result + "}";
    }

    private static string Snapshot(CompactDocument document, int handle)
    {
        var node = document.GetNode(handle);
        var result = $"{node.Kind}:{document.GetName(node.NameId)}[";
        if (node.PayloadIndex >= 0)
        {
            var payload = document.GetPayload(node.PayloadIndex);
            for (var i = 0; i < payload.AttributeCount; i++)
            {
                if (i != 0)
                    result += ",";
                var attribute = document.GetAttribute(payload.FirstAttribute + i);
                result +=
                    $"{document.GetName(attribute.NameId)}={document.GetValue(attribute.ValueStart, attribute.ValueLength)}";
            }
        }
        result += "]{";
        var child = node.FirstChild;
        while (child >= 0 && child < node.SubtreeEndExclusive)
        {
            result += Snapshot(document, child);
            child = document.GetNode(child).SubtreeEndExclusive;
        }
        return result + "}";
    }

    private static string NormalizeWhitespace(string value)
    {
        var result = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = result.Length != 0;
                continue;
            }
            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }
            result.Append(character);
        }
        return result.ToString();
    }

    private static CompactNodeKind Kind(INode node) =>
        node.NodeType switch
        {
            NodeType.Document => CompactNodeKind.Document,
            NodeType.Element => CompactNodeKind.Element,
            NodeType.ProcessingInstruction => CompactNodeKind.ProcessingInstruction,
            NodeType.Comment => CompactNodeKind.Comment,
            NodeType.Text => CompactNodeKind.Text,
            _ => CompactNodeKind.Other,
        };

    private static CompactNodeKind Kind(IReadOnlyNode node) =>
        node switch
        {
            IReadOnlyDocument => CompactNodeKind.Document,
            IReadOnlyElement => CompactNodeKind.Element,
            IReadOnlyProcessingInstructionNode => CompactNodeKind.ProcessingInstruction,
            IReadOnlyCommentNode => CompactNodeKind.Comment,
            IReadOnlyTextNode => CompactNodeKind.Text,
            _ => CompactNodeKind.Other,
        };
}
#endif
