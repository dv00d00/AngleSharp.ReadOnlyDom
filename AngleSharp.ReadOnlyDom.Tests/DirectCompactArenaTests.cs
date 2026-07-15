#if NET10_0
using System.Runtime.CompilerServices;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.ReadOnlyDom;
using AngleSharp.ReadOnlyDom.Compact;
using AngleSharp.ReadOnlyDom.Filters;
using AngleSharp.ReadOnlyDom.Html;

namespace AngleSharp.Readonly.Tests;

public sealed class CompactParserTests
{
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
        var templateParagraph = default(AngleSharp.ReadOnlyDom.Compact.Node);
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
    [Arguments(CompactDocumentLayout.FrozenColumns)]
    [Arguments(CompactDocumentLayout.Packed)]
    public async Task InterpretedExtractionPlanExplainsAndExecutesRequiredFeatures(CompactDocumentLayout layout)
    {
        const string html =
            "<main><section id=content class='selected wide' data-ready><article><a href='/item'> one <b>two</b> </a></article></section></main>";
        var plan = CompactExtractionPlan
            .Start("section")
            .WithId("content")
            .WithClass("wide")
            .WithAttribute("data-ready")
            .Child("article")
            .Descendant("a")
            .WithAttribute("href", "/item")
            .TakeFirst()
            .SelectAttribute("href", "href", required: true)
            .SelectNormalizedText("text", required: true)
            .Compile();

        using var document = CompactParser.CreateParser(layout: layout).ParseCompactDocument(html);
        var result = plan.Execute(document);
        var explanation = plan.Explain();

        await Assert.That(result.Rows.Count).IsEqualTo(1);
        await Assert.That(result.Rows[0]["href"].Span.ToString()).IsEqualTo("/item");
        await Assert.That(result.Rows[0]["href"].Ownership).IsEqualTo(CompactValueOwnership.BorrowedDocumentSlice);
        await Assert.That(result.Rows[0]["text"].ToString()).IsEqualTo("one two");
        await Assert.That(result.Rows[0]["text"].Ownership).IsEqualTo(CompactValueOwnership.Owned);
        await Assert.That(result.Counters.RowsProduced).IsEqualTo(1);
        await Assert.That(result.Counters.AttributesInspected).IsGreaterThan(0);
        await Assert.That(plan.Requirements.MetadataOptions).IsEqualTo(CompactMetadataOptions.None);
        await Assert.That(explanation).Contains("mode=interpreted compact-preorder");
        await Assert.That(explanation).Contains("termination=first valid row after path evaluation");
    }

    [Test]
    [Arguments("<div id=content><p class=item data-v=one>A<p class=item>B")]
    [Arguments("<table><div id=content><span class=item data-v=one>A</span></div></table>")]
    [Arguments("<div id=content><b><i><span class=item data-v=one>A</b>B</i></span>")]
    public async Task InterpretedExtractionPlanMatchesAngleSharpOnMalformedMarkup(string html)
    {
        var plan = CompactExtractionPlan
            .Start("div")
            .WithId("content")
            .Descendant("span")
            .WithClass("item")
            .TakeAll()
            .SelectAttribute("value", "data-v", required: true, own: true)
            .SelectNormalizedText("text")
            .Compile();
        using var expected = new HtmlParser().ParseDocument(html);
        using var actual = CompactParser.CreateParser().ParseCompactDocument(html);

        var expectedRows = expected
            .QuerySelectorAll("div#content span.item[data-v]")
            .Select(element => $"{element.GetAttribute("data-v")}|{NormalizeWhitespace(element.TextContent)}")
            .ToArray();
        var result = plan.Execute(actual);
        var actualRows = result.Rows.Select(row => $"{row["value"]}|{row["text"]}").ToArray();

        await Assert.That(actualRows).IsEquivalentTo(expectedRows);
        foreach (var row in result.Rows)
            await Assert.That(row["value"].Ownership).IsEqualTo(CompactValueOwnership.Owned);
    }

    [Test]
    public async Task ExtractionRequiredFieldsRejectOnlyMissingValues()
    {
        using var document = CompactParser
            .CreateParser()
            .ParseCompactDocument("<p data-v=''>empty</p><p>missing</p><p data-v=x>value</p>");
        var required = CompactExtractionPlan
            .Start("p")
            .TakeAll()
            .SelectAttribute("value", "data-v", required: true)
            .Compile()
            .Execute(document);
        var optional = CompactExtractionPlan
            .Start("p")
            .TakeAll()
            .SelectAttribute("value", "data-v")
            .Compile()
            .Execute(document);

        await Assert.That(required.Rows.Count).IsEqualTo(2);
        await Assert.That(required.Rows[0]["value"].Exists).IsTrue();
        await Assert.That(required.Rows[0]["value"].Span.Length).IsEqualTo(0);
        await Assert.That(required.Counters.RowsRejected).IsEqualTo(1);
        await Assert.That(optional.Rows.Count).IsEqualTo(3);
        await Assert.That(optional.Rows[1]["value"].Exists).IsFalse();
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
    public async Task StreamingExtractionMatchesConstructedDom(string html)
    {
        using var expectedDocument = new HtmlParser().ParseDocument(html);
        var expected = expectedDocument.QuerySelector("div#content");

        var actual = CompactStreamingExtractor.ExtractFirstNormalizedText(html);

        await Assert.That(actual.Found).IsEqualTo(expected is not null);
        await Assert
            .That(actual.Value.ToString())
            .IsEqualTo(NormalizeWhitespace(expected?.TextContent ?? string.Empty));
        if (actual.Found)
            await Assert.That(actual.Value.Ownership).IsEqualTo(CompactValueOwnership.Owned);
        await Assert.That(actual.Counters.TokensProcessed).IsGreaterThan(0);
        await Assert.That(actual.Counters.NodesMaterialized).IsGreaterThan(0);
        await Assert.That(actual.Counters.AttributesInspected).IsGreaterThan(0);
        await Assert.That(actual.Counters.AttributesRetained).IsGreaterThan(0);
        await Assert.That(actual.Counters.TextValuesRetained).IsGreaterThan(0);
        await Assert.That(actual.Counters.InputBytesConsumed).IsEqualTo(Encoding.UTF8.GetByteCount(html));
        await Assert.That(actual.Counters.EarlyTerminated).IsFalse();
    }

    [Test]
    public async Task StreamingExtractionFiltersUnneededAttributesAndReportsDecodedValues()
    {
        const string Html = "<main data-a=1 data-b=2><div id=content data-c=3>one &amp; two</div></main>";

        var result = CompactStreamingExtractor.ExtractFirstNormalizedText(Html);

        await Assert.That(result.Value.ToString()).IsEqualTo("one & two");
        await Assert.That(result.Counters.AttributesRetained).IsEqualTo(1);
        await Assert.That(result.Counters.ValuesDecoded).IsGreaterThan(0);
    }

    [Test]
    public async Task EofAggregateProjectsArticleAsJsonTextAndMarkdown()
    {
        const string Html = """
            <article id="content">
              <h1>Parsing <em>real</em> HTML</h1>
              <p>Use the <a href="/parser">HTML parser</a>, not regex.</p>
              <ul><li>Correct tables</li><li>Malformed markup</li></ul>
            </article>
            """;
        var article = CompactAggregateSelector.Tag("article").WithId("content");
        var plan = CompactAggregate
            .First(article)
            .Field("title", CompactAggregateProjection.FirstNormalizedText(CompactAggregateSelector.Tag("h1")))
            .Field("text", CompactAggregateProjection.SelfNormalizedText())
            .Field("markdown", CompactAggregateProjection.SelfMarkdown())
            .Compile();

        var result = plan.Execute(Html);

        await Assert.That(result.Rows.Count).IsEqualTo(1);
        await Assert.That(result.Rows[0]["title"].ToString()).IsEqualTo("Parsing real HTML");
        await Assert
            .That(result.Rows[0]["text"].ToString())
            .IsEqualTo("Parsing real HTML Use the HTML parser, not regex. Correct tablesMalformed markup");
        await Assert
            .That(result.Rows[0]["markdown"].ToString())
            .IsEqualTo(
                "# Parsing *real* HTML\n\nUse the [HTML parser](/parser), not regex.\n\n- Correct tables\n- Malformed markup"
            );
        await Assert.That(result.Rows[0]["title"].Ownership).IsEqualTo(CompactValueOwnership.Owned);
        await Assert.That(result.Counters.InputBytesConsumed).IsEqualTo(Encoding.UTF8.GetByteCount(Html));
        await Assert.That(result.Counters.RowsProduced).IsEqualTo(1);
        await Assert.That(result.ToJson()).Contains("\"markdown\":\"# Parsing *real* HTML");
        await Assert.That(plan.Explain()).Contains("termination=end-of-document");
    }

    [Test]
    public async Task EofAggregateProjectsRepeatedSearchResultObjects()
    {
        const string Html = """
            <main>
              <article class="result"><h2><a href="/a">Arena parsing</a></h2><p class="snippet">Build only what you need.</p></article>
              <article class="result"><h2><a href="/b">Compact DOM</a></h2><p class="snippet">Retain a reusable tree.</p></article>
            </main>
            """;
        var plan = CompactAggregate
            .ForEach(CompactAggregateSelector.Tag("article").WithClass("result"))
            .Field("title", CompactAggregateProjection.FirstNormalizedText(CompactAggregateSelector.Tag("h2")))
            .Field(
                "url",
                CompactAggregateProjection.FirstAttribute(CompactAggregateSelector.Tag("a"), "href"),
                required: true
            )
            .Field(
                "snippet",
                CompactAggregateProjection.FirstNormalizedText(CompactAggregateSelector.Tag("p").WithClass("snippet"))
            )
            .Compile();

        var result = plan.Execute(Html);

        await Assert.That(result.Rows.Count).IsEqualTo(2);
        await Assert.That(result.Rows[0]["title"].ToString()).IsEqualTo("Arena parsing");
        await Assert.That(result.Rows[0]["url"].ToString()).IsEqualTo("/a");
        await Assert.That(result.Rows[1]["snippet"].ToString()).IsEqualTo("Retain a reusable tree.");
        await Assert.That(result.ToJson()).StartsWith("[{\"title\":\"Arena parsing\"");
        await Assert.That(result.Counters.RowsRejected).IsEqualTo(0);
    }

    [Test]
    public async Task EofAggregateRequiredAttributeDistinguishesEmptyFromMissing()
    {
        const string Html =
            "<article class=result><a href=''>empty</a></article><article class=result><a>missing</a></article>";
        var plan = CompactAggregate
            .ForEach(CompactAggregateSelector.Tag("article").WithClass("result"))
            .Field(
                "url",
                CompactAggregateProjection.FirstAttribute(CompactAggregateSelector.Tag("a"), "href"),
                required: true
            )
            .Compile();

        var result = plan.Execute(Html);

        await Assert.That(result.Rows.Count).IsEqualTo(1);
        await Assert.That(result.Rows[0]["url"].Exists).IsTrue();
        await Assert.That(result.Rows[0]["url"].Span.Length).IsEqualTo(0);
        await Assert.That(result.Counters.RowsRejected).IsEqualTo(1);
    }

    [Test]
    public async Task EofAggregateMarkdownSupportsExclusionsAndPreservedCode()
    {
        const string Html = """
            <main id="docs">
              <nav>Previous | Next</nav>
              <h1>Authentication</h1>
              <p>Send an <code>Authorization</code> header.</p>
              <pre><code>curl -H "Authorization: Bearer TOKEN"</code></pre>
              <aside>Internal advertisement</aside>
            </main>
            """;
        var plan = CompactAggregate
            .First(CompactAggregateSelector.Tag("main").WithId("docs"))
            .Field(
                "markdown",
                CompactAggregateProjection.SelfMarkdown(
                    CompactAggregateSelector.Tag("nav"),
                    CompactAggregateSelector.Tag("aside")
                )
            )
            .Compile();

        var result = plan.Execute(Html);

        await Assert
            .That(result.Rows[0]["markdown"].ToString())
            .IsEqualTo(
                "# Authentication\n\nSend an `Authorization` header.\n\n```text\ncurl -H \"Authorization: Bearer TOKEN\"\n```"
            );
        await Assert.That(result.Rows[0]["markdown"].ToString()).DoesNotContain("Previous");
        await Assert.That(result.Rows[0]["markdown"].ToString()).DoesNotContain("advertisement");
    }

    [Test]
    [Arguments("<article class=result><b><i>one</b> two</i><p>three")]
    [Arguments("<table><article class=result>before<span>inside</span></article><tr><td>cell</table>")]
    public async Task EofAggregateNormalizedTextMatchesFinalAngleSharpTopology(string html)
    {
        using var expectedDocument = new HtmlParser().ParseDocument(html);
        var expected = expectedDocument.QuerySelector("article.result");
        var plan = CompactAggregate
            .First(CompactAggregateSelector.Tag("article").WithClass("result"))
            .Field("text", CompactAggregateProjection.SelfNormalizedText())
            .Compile();

        var actual = plan.Execute(html);

        await Assert.That(actual.Rows.Count).IsEqualTo(expected is null ? 0 : 1);
        if (expected is not null)
            await Assert.That(actual.Rows[0]["text"].ToString()).IsEqualTo(NormalizeWhitespace(expected.TextContent));
    }

    [Test]
    public async Task StreamingExtractionReportsMissingTarget()
    {
        var result = CompactStreamingExtractor.ExtractFirstNormalizedText("<main><p>none</p></main>");

        await Assert.That(result.Found).IsFalse();
        await Assert.That(result.Value.Exists).IsFalse();
        await Assert.That(result.Value.Ownership).IsEqualTo(CompactValueOwnership.None);
    }

    [Test]
    public async Task AppendOnlyDocumentsFreezeColumnsByDefault()
    {
        using var document = CompactParser.CreateParser().ParseCompactDocument("<main><p>x</p></main>");

        await Assert.That(document.Layout).IsEqualTo(CompactDocumentLayout.FrozenColumns);
    }

    [Test]
    public async Task DefaultContextSupportsTheSameExtensionPatternAsReadOnlyDom()
    {
        var parser = new HtmlParser(default, CompactParser.DefaultContext);
        using var document = parser.ParseCompactDocument("<main><p>x</p></main>");

        await Assert.That(document.Elements("main").Count()).IsEqualTo(1);
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
        await Assert.That(Unsafe.SizeOf<SmallReferenceList4<object>>()).IsEqualTo(48);
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

        await Assert.That(actualContent.Text()).IsEqualTo(expected.QuerySelector("#content")!.TextContent);
        await Assert.That(actualTemplate.Text()).IsEqualTo(expected.QuerySelector("template")!.TextContent);
        await Assert.That(actualTemplate.TextLength()).IsEqualTo(0);
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
    }

    [Test]
    public async Task PooledDocumentOwnsAndReturnsItsBuffers()
    {
        var document = CompactParser.CreateParser().ParseCompactDocument("<main><p>x</p></main>");
        await Assert.That(document.NodeCount).IsGreaterThan(3);
        await Assert.That(document.FindNameId("main")).IsNotEqualTo(ushort.MaxValue);
        document.Dispose();
        document.Dispose();
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
    public async Task ExtractionProfileDisablesNonExtractionFeatures()
    {
        var options = CompactParserProfiles.Extraction;

        await Assert.That(options.IsScripting).IsFalse();
        await Assert.That(options.IsNotSupportingFrames).IsTrue();
        await Assert.That(options.IsKeepingSourceReferences).IsFalse();
        await Assert.That(options.IsPreservingAttributeNames).IsFalse();
        await Assert.That(options.DisableElementPositionTracking).IsTrue();
        await Assert.That(options.SkipComments).IsTrue();
        await Assert.That(options.SkipProcessingInstructions).IsTrue();
        await Assert.That(options.SkipScriptText).IsTrue();
        await Assert.That(options.SkipRawText).IsTrue();
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
    public async Task SubtreeMiddlewareDoesNotCountHtmlVoidElementsAsOpenScopes()
    {
        const string html = "<body><section id='target'><img><input><p>x</p></section><aside>tail</aside></body>";
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
