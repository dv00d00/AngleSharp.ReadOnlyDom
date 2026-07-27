#if NET10_0

using System.Text;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Compact;
using AngleSharp.ReadOnlyDom.Filters;
using AngleSharp.ReadOnlyDom.Html;
using AngleSharp.ReadOnlyDom.Streaming;
using BenchmarkDotNet.Attributes;
using AngleSharpDocument = AngleSharp.Dom.IDocument;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

/// <summary>
/// Turns the README's pathological ParserBenchmark [UrlTest=qq] fixture into a realistic scraper.
/// Every lane returns the same owned objects for article links inside QQ news-list cards.
/// </summary>
[BenchmarkCategory("Extraction", "Query")]
[MemoryDiagnoser]
public class QqArticleScraperBenchmark
{
    private static readonly QueryPlan<ArticleStateMachine> ArticleQuery = CreateArticleQuery();
    private static readonly QueryPlan<ArticleStateMachine> CompletedArticleQuery = CreateCompletedArticleQuery();
    private readonly HtmlParser _angleSharp = new();
    private readonly HtmlParser _angleSharpScraper = new(CreateOptions(trackSources: false));
    private readonly HtmlParser _readOnlyMinimal = CreateReadOnlyParser(ReadOnlyMetadataProfile.Minimal);

    private readonly HtmlParser _compact = CompactParser.CreateParser(
        parserOptions: CreateOptions(trackSources: false)
    );

    private string _html = null!;
    private byte[] _utf8 = null!;
    private List<Article> _expected = null!;

    [Params("qq.html", "qq-x4.html")]
    public string File { get; set; } = "qq.html";

    [GlobalSetup]
    public void Setup()
    {
        var source = BenchmarkCorpus.Load("full").Single(static document => document.Name == "qq").Html;
        var copies = File switch
        {
            "qq.html" => 1,
            "qq-x4.html" => 4,
            _ => throw new InvalidOperationException($"Unknown QQ scraper fixture: {File}"),
        };
        _html = copies == 1 ? source : RepeatBody(source, copies);
        _utf8 = Encoding.UTF8.GetBytes(_html);
        _expected = AngleSharpDom();

        AssertEqual(nameof(AngleSharpScraperOptionsCss), AngleSharpScraperOptionsCss());
        AssertEqual(nameof(ReadOnlyMinimalBodyFiltered), ReadOnlyMinimalBodyFiltered());
        AssertEqual(nameof(CompactFrozenResolvedIds), CompactFrozenResolvedIds());
        AssertEqual(nameof(QueryCompiledUtf8Fold), QueryCompiledUtf8Fold());
        AssertEqual(nameof(QueryCompletedElementFold), QueryCompletedElementFold());

        Console.WriteLine(
            $"QQ scraper fixture {File}: {_utf8.Length:N0} UTF-8 bytes, "
                + $"{_expected.Count:N0} article-link objects."
        );
    }
    
    public List<Article> AngleSharpDom()
    {
        using var document = _angleSharp.ParseDocument(_html);
        return ScrapeAngleSharpCss(document);
    }

    [Benchmark(Baseline = true)]
    public List<Article> AngleSharpScraperOptionsCss()
    {
        using var document = _angleSharpScraper.ParseDocument(_html);
        return ScrapeAngleSharpCss(document);
    }

    private static List<Article> ScrapeAngleSharpCss(AngleSharpDocument document)
    {
        var output = new List<Article>(128);
        foreach (var card in document.QuerySelectorAll(".news-list li[dt-eid='em_item_article']"))
        {
            var metadata = card.GetAttribute("dt-params") ?? string.Empty;
            foreach (var link in card.QuerySelectorAll("a[href]"))
            {
                var image = link.QuerySelector("img");
                AddArticle(
                    output,
                    link.TextContent,
                    link.GetAttribute("href") ?? string.Empty,
                    image?.GetAttribute("src"),
                    image?.GetAttribute("alt"),
                    metadata
                );
            }
        }

        return output;
    }

    private static string RepeatBody(string source, int copies)
    {
        var bodyOpen = source.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        var bodyContent = bodyOpen < 0 ? -1 : source.IndexOf('>', bodyOpen) + 1;
        var bodyClose = source.LastIndexOf("</body", StringComparison.OrdinalIgnoreCase);
        if (bodyContent <= 0 || bodyClose < bodyContent)
            throw new InvalidOperationException("QQ scraper fixture does not contain a complete body element.");

        var bodyLength = bodyClose - bodyContent;
        var output = new StringBuilder(source.Length + bodyLength * (copies - 1));
        output.Append(source.AsSpan(0, bodyContent));
        for (var copy = 0; copy < copies; copy++)
            output.Append(source.AsSpan(bodyContent, bodyLength));
        output.Append(source.AsSpan(bodyClose));
        return output.ToString();
    }

    [Benchmark]
    public List<Article> ReadOnlyMinimalBodyFiltered()
    {
        var filter = new FirstTagAndAllChildren("body");
        using var document = _readOnlyMinimal.ParseReadOnlyDocument(_html, filter.Loop);
        return ScrapeReadOnly(document);
    }

    [Benchmark]
    public List<Article> CompactFrozenResolvedIds()
    {
        var filter = new FirstTagAndAllChildren("body");
        using var document = _compact.ParseCompactDocument(_html, filter.Loop);
        var ul = document.Name("ul");
        var li = document.Name("li");
        var anchor = document.Name("a");
        var image = document.Name("img");
        var className = document.Name("class");
        var cardKind = document.Name("dt-eid");
        var metadataName = document.Name("dt-params");
        var href = document.Name("href");
        var source = document.Name("src");
        var alternateText = document.Name("alt");
        var output = new List<Article>(128);
        var text = new StringBuilder();

        foreach (var list in document.Elements(ul).WithClass(className, "news-list"))
        {
            foreach (var card in list.Elements(li).WithAttribute(cardKind, "em_item_article"))
            {
                var metadata = card.Attr(metadataName).ToString();
                foreach (var link in card.Elements(anchor).WithAttribute(href))
                {
                    var linkImage = link.Elements(image).First();
                    text.Clear();
                    link.AppendText(text);
                    AddArticle(
                        output,
                        text.ToString(),
                        link.Attr(href).ToString(),
                        linkImage.Exists ? linkImage.Attr(source).ToString() : null,
                        linkImage.Exists ? linkImage.Attr(alternateText).ToString() : null,
                        metadata
                    );
                }
            }
        }

        return output;
    }
    
    [Benchmark]
    public List<Article> QueryCompiledUtf8Fold()
    {
        var state = ArticleQuery.Execute(_utf8, new ArticleStateMachine(), Utf8InputContract.WellFormedUtf8);
        return state.DetachResults();
    }

    [Benchmark]
    public List<Article> QueryCompletedElementFold()
    {
        var state = CompletedArticleQuery.Execute(_utf8, new ArticleStateMachine(), Utf8InputContract.WellFormedUtf8);
        return state.DetachResults();
    }

    private static QueryPlan<ArticleStateMachine> CreateCompletedArticleQuery()
    {
        var list = StreamQuery.For<ArticleStateMachine>("ul").Class("news-list");
        var card = list.Descendant("li")
            .Attribute("dt-eid", "em_item_article")
            .OnStart(static (ref state, in element) => state.StartCard(element), "dt-params")
            .OnEnd(static (ref state) => state.EndCard());

        var link = card.Descendant("a")
            .Attribute("href")
            .OnNormalizedText(
                static (ref state, in element) =>
                {
                    AddArticle(
                        state._results,
                        element.GetText(),
                        element.GetAttributeOrEmpty("href"),
                        state._imageUrl,
                        state._imageAlt,
                        state._cardMetadata
                    );
                    state._imageUrl = null;
                    state._imageAlt = null;
                }
            );

        link.Descendant("img")
            .OnClose(
                static (ref state, in element) =>
                {
                    state._imageUrl ??= element.GetAttribute("src");
                    state._imageAlt ??= element.GetAttribute("alt");
                },
                "src",
                "alt"
            );
        return list.Compile();
    }

    private static QueryPlan<ArticleStateMachine> CreateArticleQuery()
    {
        var list = StreamQuery.For<ArticleStateMachine>("ul").Class("news-list");
        
        var card = list.Descendant("li").Attribute("dt-eid", "em_item_article")
            .OnStart(static (ref state, in element) => state.StartCard(element), "dt-params")
            .OnEnd(static (ref state) => state.EndCard());
        
        var link = card.Descendant("a")
            .Attribute("href")
            .OnStart(static (ref state, in element) => state.StartLink(element), "href")
            .OnText(static (ref state, text) => state.AppendText(text))
            .OnEnd(static (ref state) => state.EndLink());
        
        link.Descendant("img").OnStart(static (ref state, in element) => state.Image(element), "src", "alt");
        return list.Compile();
    }

    private static List<Article> ScrapeReadOnly(IReadOnlyNode document)
    {
        var output = new List<Article>(128);
        foreach (
            var card in document.QueryAll(
                static node => node.TagClass("ul", "news-list"),
                static node => node.Tag("li") && node.Attr("dt-eid", "em_item_article")
            )
        )
        {
            var metadata = Attribute(card, "dt-params");
            foreach (var link in card.QueryAll(static node => node.Tag("a") && node.Attr("href")))
            {
                var image = link.QueryOne(static node => node.Tag("img"));
                AddArticle(
                    output,
                    link.GetTextContent(),
                    Attribute(link, "href"),
                    image is null ? null : Attribute(image, "src"),
                    image is null ? null : Attribute(image, "alt"),
                    metadata
                );
            }
        }

        return output;
    }

    private static string Attribute(IReadOnlyNode node, string name) =>
        node is IReadOnlyElement element ? element.Attributes[name]?.Value.ToString() ?? string.Empty : string.Empty;

    private static void AddArticle(
        List<Article> output,
        string title,
        string url,
        string? imageUrl,
        string? imageAlt,
        string metadata
    )
    {
        title = Normalize(title);
        imageAlt = NullIfEmpty(imageAlt);
        if (title.Length == 0)
            title = imageAlt ?? string.Empty;
        if (title.Length == 0 || string.IsNullOrWhiteSpace(url))
            return;

        output.Add(new Article(title, url, NullIfEmpty(imageUrl), imageAlt, metadata));
    }

    private void AssertEqual(string lane, List<Article> actual)
    {
        if (!_expected.SequenceEqual(actual))
        {
            var mismatch = Enumerable
                .Range(0, Math.Min(_expected.Count, actual.Count))
                .FirstOrDefault(index => _expected[index] != actual[index], -1);
            throw new InvalidOperationException(
                $"{lane} disagrees with AngleSharp: expected={_expected.Count}, actual={actual.Count}, "
                    + $"first mismatch={mismatch}."
            );
        }
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var output = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = output.Length != 0;
                continue;
            }

            if (pendingSpace)
            {
                output.Append(' ');
                pendingSpace = false;
            }

            output.Append(character);
        }

        return output.ToString();
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static HtmlParser CreateReadOnlyParser(ReadOnlyMetadataProfile profile) =>
        new(
            CreateOptions(profile is ReadOnlyMetadataProfile.SourceMapped or ReadOnlyMetadataProfile.Diagnostic),
            ReadOnlyParser.CreateContext(profile)
        );

    private static HtmlParserOptions CreateOptions(bool trackSources) =>
        new()
        {
            IsKeepingSourceReferences = trackSources,
            SkipComments = true,
            SkipProcessingInstructions = true,
            SkipScriptText = true,
            SkipRawText = true,
            SkipPlaintext = true,
            SkipCDATA = true,
            DisableElementPositionTracking = !trackSources,
            ShouldEmitAttribute = static (ref _, name) => name.Span is "class" or "dt-eid" or "dt-params" or "href" or "src" or "alt",
        };

    public sealed record Article(string Title, string Url, string? ImageUrl, string? ImageAlt, string CardMetadata);

    private sealed class ArticleStateMachine
    {
        internal readonly StringBuilder _title = new();
        internal List<Article> _results = new(128);
        internal string _cardMetadata = string.Empty;
        internal string? _href;
        internal string? _imageUrl;
        internal string? _imageAlt;

        public void StartCard(in Element element) => _cardMetadata = Attr(element, "dt-params") ?? string.Empty;

        public void EndCard() => _cardMetadata = string.Empty;

        public void StartLink(in Element element)
        {
            _href = Attr(element, "href");
            _imageUrl = null;
            _imageAlt = null;
            _title.Clear();
        }

        public void AppendText(ReadOnlySpan<byte> utf8)
        {
            Span<char> chars = stackalloc char[2];
            while (!utf8.IsEmpty)
            {
                Rune.DecodeFromUtf8(utf8, out var rune, out var consumed);
                var written = rune.EncodeToUtf16(chars);
                _title.Append(chars[..written]);
                utf8 = utf8[consumed..];
            }
        }

        public void Image(in Element element)
        {
            _imageUrl ??= Attr(element, "src");
            _imageAlt ??= Attr(element, "alt");
        }

        public void EndLink()
        {
            if (_href is not null)
                AddArticle(_results, _title.ToString(), _href, _imageUrl, _imageAlt, _cardMetadata);
            
            _href = null;
            _imageUrl = null;
            _imageAlt = null;
            _title.Clear();
        }

        public List<Article> DetachResults()
        {
            var results = _results;
            _results = [];
            return results;
        }

        static string? Attr(in Element element, string attribute) =>
            element.TryGetAttribute(attribute, out var value) ? Encoding.UTF8.GetString(value) : null;
    }
}
#endif
