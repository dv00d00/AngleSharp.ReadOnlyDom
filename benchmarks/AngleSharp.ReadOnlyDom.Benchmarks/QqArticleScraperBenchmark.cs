#if NET10_0

using System.Buffers;
using System.Text;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Compact;
using AngleSharp.ReadOnlyDom.Compact.Experimental;
using AngleSharp.ReadOnlyDom.Filters;
using AngleSharp.ReadOnlyDom.Html;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.ReadOnlyDom.Streaming.Utf8Stream;
using AngleSharp.ReadOnlyDom.Streaming.Utf8Stream.Query;
using AngleSharp.Text;
using BenchmarkDotNet.Attributes;
using AngleSharpDocument = AngleSharp.Dom.IDocument;
using AngleSharpElement = AngleSharp.Dom.IElement;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

/// <summary>
/// Turns the README's pathological ParserBenchmark [UrlTest=qq] fixture into a realistic scraper.
/// Every lane returns the same owned objects for article links inside QQ news-list cards.
/// </summary>
[MemoryDiagnoser]
public class QqArticleScraperBenchmark
{
    private static readonly ISelector ArticleCardsSelector = ParseSelector(".news-list li[dt-eid='em_item_article']");
    private static readonly ISelector ArticleLinksSelector = ParseSelector("a[href]");
    private static readonly ISelector ImagesSelector = ParseSelector("img");
    private static readonly QueryPlan<CompiledArticleState> ArticleQuery = CreateArticleQuery();
    private static readonly QueryPlan<CompiledArticleState> CompletedArticleQuery = CreateCompletedArticleQuery();
    private readonly HtmlParser _angleSharp = new();
    private readonly HtmlParser _angleSharpScraper = new(CreateOptions(trackSources: false));
    private readonly HtmlParser _readOnlyMinimal = CreateReadOnlyParser(ReadOnlyMetadataProfile.Minimal);
    private readonly HtmlParser _readOnlySourceMapped = CreateReadOnlyParser(ReadOnlyMetadataProfile.SourceMapped);

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

        AssertEqual(nameof(AngleSharpDomApi), AngleSharpDomApi());
        AssertEqual(nameof(AngleSharpDomTreeWalk), AngleSharpDomTreeWalk());
        AssertEqual(nameof(AngleSharpScraperOptionsCss), AngleSharpScraperOptionsCss());
        AssertEqual(nameof(AngleSharpScraperOptionsPrecompiledCss), AngleSharpScraperOptionsPrecompiledCss());
        AssertEqual(nameof(AngleSharpScraperOptionsDomApi), AngleSharpScraperOptionsDomApi());
        AssertEqual(nameof(AngleSharpScraperOptionsTreeWalk), AngleSharpScraperOptionsTreeWalk());
        AssertEqual(nameof(AngleSharpUtf16MemoryCss), AngleSharpUtf16MemoryCss());
        AssertEqual(nameof(AngleSharpUtf8MemoryAutoCss), AngleSharpUtf8MemoryAutoCss());
        AssertEqual(nameof(AngleSharpUtf8MemoryExplicitCss), AngleSharpUtf8MemoryExplicitCss());
        AssertEqual(nameof(AngleSharpLegacyStreamCss), AngleSharpLegacyStreamCss());
        AssertEqual(nameof(AngleSharpBufferedStreamCss), AngleSharpBufferedStreamCss().GetAwaiter().GetResult());
        AssertEqual(nameof(AngleSharpStreamingStreamCss), AngleSharpStreamingStreamCss().GetAwaiter().GetResult());
        AssertEqual(nameof(ReadOnlyMinimalFull), ReadOnlyMinimalFull());
        AssertEqual(nameof(ReadOnlyMinimalBodyFiltered), ReadOnlyMinimalBodyFiltered());
        AssertEqual(nameof(ReadOnlySourceMappedBodyFiltered), ReadOnlySourceMappedBodyFiltered());
        AssertEqual(nameof(CompactFrozenBodyFiltered), CompactFrozenBodyFiltered());
        AssertEqual(nameof(NativeUtf8Fold), NativeUtf8Fold());
        AssertEqual(nameof(QueryCompiledUtf8Fold), QueryCompiledUtf8Fold());
        AssertEqual(nameof(QueryCompletedElementFold), QueryCompletedElementFold());

        Console.WriteLine(
            $"QQ scraper fixture {File}: {_utf8.Length:N0} UTF-8 bytes, "
                + $"{_expected.Count:N0} article-link objects."
        );
    }

    [Benchmark(Baseline = true)]
    public List<Article> AngleSharpDom()
    {
        using var document = _angleSharp.ParseDocument(_html);
        return ScrapeAngleSharpCss(document);
    }

    [Benchmark]
    public List<Article> AngleSharpDomApi()
    {
        using var document = _angleSharp.ParseDocument(_html);
        return ScrapeAngleSharpDomApi(document);
    }

    [Benchmark]
    public List<Article> AngleSharpDomTreeWalk()
    {
        using var document = _angleSharp.ParseDocument(_html);
        return ScrapeAngleSharpTreeWalk(document);
    }

    [Benchmark]
    public List<Article> AngleSharpScraperOptionsCss()
    {
        using var document = _angleSharpScraper.ParseDocument(_html);
        return ScrapeAngleSharpCss(document);
    }

    [Benchmark]
    public List<Article> AngleSharpScraperOptionsPrecompiledCss()
    {
        using var document = _angleSharpScraper.ParseDocument(_html);
        return ScrapeAngleSharpPrecompiledCss(document);
    }

    [Benchmark]
    public List<Article> AngleSharpScraperOptionsDomApi()
    {
        using var document = _angleSharpScraper.ParseDocument(_html);
        return ScrapeAngleSharpDomApi(document);
    }

    [Benchmark]
    public List<Article> AngleSharpScraperOptionsTreeWalk()
    {
        using var document = _angleSharpScraper.ParseDocument(_html);
        return ScrapeAngleSharpTreeWalk(document);
    }

    [Benchmark]
    public List<Article> AngleSharpUtf16MemoryCss()
    {
        using var document = _angleSharpScraper.ParseDocument(_html.AsMemory());
        return ScrapeAngleSharpCss(document);
    }

    [Benchmark]
    public List<Article> AngleSharpUtf8MemoryAutoCss()
    {
        using var document = _angleSharpScraper.ParseDocument(new TextSource(new ReadOnlyByteTextSource(_utf8)));
        return ScrapeAngleSharpCss(document);
    }

    [Benchmark]
    public List<Article> AngleSharpUtf8MemoryExplicitCss()
    {
        using var document = _angleSharpScraper.ParseDocument(
            new TextSource(new ReadOnlyByteTextSource(_utf8, Encoding.UTF8))
        );
        return ScrapeAngleSharpCss(document);
    }

    [Benchmark]
    public List<Article> AngleSharpLegacyStreamCss()
    {
        using var stream = new MemoryStream(_utf8, writable: false);
        using var document = _angleSharpScraper.ParseDocument(stream);
        return ScrapeAngleSharpCss(document);
    }

    [Benchmark]
    public async Task<List<Article>> AngleSharpBufferedStreamCss()
    {
        using var stream = new MemoryStream(_utf8, writable: false);
        using var document = await _angleSharpScraper
            .ParseDocumentAsync(stream, HtmlStreamSourceMode.Buffered, Encoding.UTF8)
            .ConfigureAwait(false);
        return ScrapeAngleSharpCss(document);
    }

    [Benchmark]
    public async Task<List<Article>> AngleSharpStreamingStreamCss()
    {
        using var stream = new MemoryStream(_utf8, writable: false);
        using var document = await _angleSharpScraper
            .ParseDocumentAsync(stream, HtmlStreamSourceMode.Streaming, Encoding.UTF8)
            .ConfigureAwait(false);
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

    private static List<Article> ScrapeAngleSharpDomApi(AngleSharpDocument document)
    {
        var output = new List<Article>(128);
        foreach (var list in document.GetElementsByClassName("news-list"))
        {
            if (!list.LocalName.Equals("ul", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var card in list.GetElementsByTagName("li"))
            {
                if (!string.Equals(card.GetAttribute("dt-eid"), "em_item_article", StringComparison.Ordinal))
                    continue;

                var metadata = card.GetAttribute("dt-params") ?? string.Empty;
                foreach (var link in card.GetElementsByTagName("a"))
                {
                    var href = link.GetAttribute("href");
                    if (href is null)
                        continue;

                    var image = link.GetElementsByTagName("img").FirstOrDefault();
                    AddArticle(
                        output,
                        link.TextContent,
                        href,
                        image?.GetAttribute("src"),
                        image?.GetAttribute("alt"),
                        metadata
                    );
                }
            }
        }

        return output;
    }

    private static List<Article> ScrapeAngleSharpPrecompiledCss(AngleSharpDocument document)
    {
        var output = new List<Article>(128);
        foreach (var card in AngleSharp.Dom.QueryExtensions.QuerySelectorAll(document.ChildNodes, ArticleCardsSelector))
        {
            var metadata = card.GetAttribute("dt-params") ?? string.Empty;
            foreach (var link in AngleSharp.Dom.QueryExtensions.QuerySelectorAll(card.ChildNodes, ArticleLinksSelector))
            {
                var image = AngleSharp
                    .Dom.QueryExtensions.QuerySelectorAll(link.ChildNodes, ImagesSelector)
                    .FirstOrDefault();
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

    private static List<Article> ScrapeAngleSharpTreeWalk(AngleSharpDocument document)
    {
        var output = new List<Article>(128);
        WalkForCards(document.DocumentElement, newsListDepth: 0, output);
        return output;
    }

    private static void WalkForCards(AngleSharpElement element, int newsListDepth, List<Article> output)
    {
        if (
            element.LocalName.Equals("ul", StringComparison.OrdinalIgnoreCase)
            && element.ClassList.Contains("news-list")
        )
        {
            newsListDepth++;
        }

        if (
            newsListDepth != 0
            && element.LocalName.Equals("li", StringComparison.OrdinalIgnoreCase)
            && string.Equals(element.GetAttribute("dt-eid"), "em_item_article", StringComparison.Ordinal)
        )
        {
            WalkForLinks(element, element.GetAttribute("dt-params") ?? string.Empty, output);
        }

        foreach (var child in element.Children)
            WalkForCards(child, newsListDepth, output);
    }

    private static void WalkForLinks(AngleSharpElement element, string metadata, List<Article> output)
    {
        foreach (var child in element.Children)
        {
            if (
                child.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase)
                && child.GetAttribute("href") is { } href
            )
            {
                var image = FindFirstImage(child);
                AddArticle(
                    output,
                    child.TextContent,
                    href,
                    image?.GetAttribute("src"),
                    image?.GetAttribute("alt"),
                    metadata
                );
            }

            WalkForLinks(child, metadata, output);
        }
    }

    private static AngleSharpElement? FindFirstImage(AngleSharpElement element)
    {
        foreach (var child in element.Children)
        {
            if (child.LocalName.Equals("img", StringComparison.OrdinalIgnoreCase))
                return child;

            if (FindFirstImage(child) is { } descendant)
                return descendant;
        }

        return null;
    }

    private static ISelector ParseSelector(string selector) =>
        new CssSelectorParser().ParseSelector(selector)
        ?? throw new InvalidOperationException($"Invalid benchmark selector: {selector}");

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
    public List<Article> ReadOnlyMinimalFull()
    {
        using var document = _readOnlyMinimal.ParseReadOnlyDocument(_html);
        return ScrapeReadOnly(document);
    }

    [Benchmark]
    public List<Article> ReadOnlyMinimalBodyFiltered()
    {
        var filter = new FirstTagAndAllChildren("body");
        using var document = _readOnlyMinimal.ParseReadOnlyDocument(_html, filter.Loop);
        return ScrapeReadOnly(document);
    }

    [Benchmark]
    public List<Article> ReadOnlySourceMappedBodyFiltered()
    {
        var filter = new FirstTagAndAllChildren("body");
        using var document = _readOnlySourceMapped.ParseReadOnlyDocument(_html, filter.Loop);
        return ScrapeReadOnly(document);
    }

    [Benchmark]
    public List<Article> CompactFrozenBodyFiltered()
    {
        var filter = new FirstTagAndAllChildren("body");
        using var document = _compact.ParseCompactDocument(_html, filter.Loop);
        var output = new List<Article>(128);
        foreach (var list in document.Elements("ul").WithClass("news-list"))
        {
            foreach (var card in list.Elements("li").WithAttribute("dt-eid", "em_item_article"))
            {
                var metadata = card.Attr("dt-params").ToString();
                foreach (var link in card.Elements("a").WithAttribute("href"))
                {
                    var image = link.Elements("img").First();
                    AddArticle(
                        output,
                        link.Text(),
                        link.Attr("href").ToString(),
                        image.Exists ? image.Attr("src").ToString() : null,
                        image.Exists ? image.Attr("alt").ToString() : null,
                        metadata
                    );
                }
            }
        }

        return output;
    }

    [Benchmark]
    public List<Article> NativeUtf8Fold()
    {
        using var sink = new NativeArticleSink();
        var tokenizer = new Utf8HtmlTokenizer(sink);
        tokenizer.Write(_utf8);
        tokenizer.Complete();
        return sink.DetachResults();
    }

    [Benchmark]
    public List<Article> QueryCompiledUtf8Fold()
    {
        var state = ArticleQuery.Execute(_utf8, new CompiledArticleState());
        return state.DetachResults();
    }

    [Benchmark]
    public List<Article> QueryCompletedElementFold()
    {
        var state = CompletedArticleQuery.Execute(_utf8, new CompiledArticleState());
        return state.DetachResults();
    }

    private static QueryPlan<CompiledArticleState> CreateCompletedArticleQuery()
    {
        var list = StreamQuery.For<CompiledArticleState>("ul").WithClass("news-list");
        var card = list.Descendant("li")
            .WithAttribute("dt-eid", "em_item_article")
            .OnStart(static (ref state, in element) => state.StartCard(element), "dt-params")
            .OnEnd(static (ref state) => state.EndCard());

        var link = card.Descendant("a")
            .WithAttribute("href")
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

    private static QueryPlan<CompiledArticleState> CreateArticleQuery()
    {
        var list = QueryNode<CompiledArticleState>.Root(Selector.Tag("ul").WithClass("news-list"));
        var card = list.Descendant(Selector.Tag("li").WithAttribute("dt-eid", "em_item_article"))
            .OnStart(static (ref state, in element) => state.StartCard(element), "dt-params")
            .OnEnd(static (ref state) => state.EndCard());
        var link = card.Descendant(Selector.Tag("a").WithAttribute("href"))
            .OnStart(static (ref state, in element) => state.StartLink(element), "href")
            .OnText(static (ref state, text) => state.AppendText(text))
            .OnEnd(static (ref state) => state.EndLink());
        link.Descendant(Selector.Tag("img"))
            .OnStart(static (ref state, in element) => state.Image(element), "src", "alt");
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
            ShouldEmitAttribute = static (ref _, name) =>
                name.Span is "class" or "dt-eid" or "dt-params" or "href" or "src" or "alt",
        };

    public sealed record Article(string Title, string Url, string? ImageUrl, string? ImageAlt, string CardMetadata);

    private sealed class CompiledArticleState
    {
        internal readonly StringBuilder _title = new();
        internal List<Article> _results = new(128);
        internal string _cardMetadata = string.Empty;
        internal string? _href;
        internal string? _imageUrl;
        internal string? _imageAlt;

        public void StartCard(in Element element) => _cardMetadata = Decode(element, "dt-params") ?? string.Empty;

        public void EndCard() => _cardMetadata = string.Empty;

        public void StartLink(in Element element)
        {
            _href = Decode(element, "href");
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
            _imageUrl ??= Decode(element, "src");
            _imageAlt ??= Decode(element, "alt");
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

        internal static string? Decode(in Element element, string attribute) =>
            element.TryGetAttribute(attribute, out var value) ? Encoding.UTF8.GetString(value) : null;
    }

    private sealed class NativeArticleSink : IUtf8HtmlTokenSink, IDisposable
    {
        private Frame[] _frames = ArrayPool<Frame>.Shared.Rent(64);
        private int _frameCount;
        private int _newsListDepth;
        private int _cardDepth;
        private bool _pendingNewsList;
        private bool _pendingCard;
        private TagKind _pendingKind;
        private ulong _pendingHash;
        private int _pendingLength;
        private string? _pendingMetadata;
        private string? _pendingHref;
        private string? _pendingImageUrl;
        private string? _pendingImageAlt;
        private string _cardMetadata = string.Empty;
        private string? _activeHref;
        private string? _activeImageUrl;
        private string? _activeImageAlt;
        private readonly StringBuilder _title = new();
        private List<Article> _results = new(128);
        private bool _disposed;

        public void StartTag(Utf8HtmlName name)
        {
            _pendingKind = Kind(name);
            _pendingHash = name.SemanticHash;
            _pendingLength = name.Verbatim.Length;
            _pendingNewsList = false;
            _pendingCard = false;
            _pendingMetadata = null;
            _pendingHref = null;
            _pendingImageUrl = null;
            _pendingImageAlt = null;
        }

        public bool WantsAttribute(Utf8HtmlName name) =>
            _pendingKind switch
            {
                TagKind.Ul => name.SemanticEquals("class"u8),
                TagKind.Li when _newsListDepth != 0 =>
                    name.SemanticEquals("dt-eid"u8) || name.SemanticEquals("dt-params"u8),
                TagKind.A when _cardDepth != 0 => name.SemanticEquals("href"u8),
                TagKind.Img when _activeHref is not null =>
                    name.SemanticEquals("src"u8) || name.SemanticEquals("alt"u8),
                _ => false,
            };

        public void Attribute(Utf8HtmlName name, ReadOnlySpan<byte> value)
        {
            switch (_pendingKind)
            {
                case TagKind.Ul when name.SemanticEquals("class"u8):
                    _pendingNewsList = ContainsToken(value, "news-list"u8);
                    break;
                case TagKind.Li when _newsListDepth != 0 && name.SemanticEquals("dt-eid"u8):
                    _pendingCard = value.SequenceEqual("em_item_article"u8);
                    break;
                case TagKind.Li when _newsListDepth != 0 && name.SemanticEquals("dt-params"u8):
                    _pendingMetadata = Encoding.UTF8.GetString(value);
                    break;
                case TagKind.A when _cardDepth != 0 && name.SemanticEquals("href"u8):
                    _pendingHref = Encoding.UTF8.GetString(value);
                    break;
                case TagKind.Img when _activeHref is not null && name.SemanticEquals("src"u8):
                    _pendingImageUrl = Encoding.UTF8.GetString(value);
                    break;
                case TagKind.Img when _activeHref is not null && name.SemanticEquals("alt"u8):
                    _pendingImageAlt = Encoding.UTF8.GetString(value);
                    break;
            }
        }

        public void StartTagEnd(bool selfClosing)
        {
            var newsList = _pendingKind == TagKind.Ul && _pendingNewsList;
            var card = _pendingKind == TagKind.Li && _newsListDepth != 0 && _pendingCard;
            var articleLink = _pendingKind == TagKind.A && _cardDepth != 0 && !string.IsNullOrEmpty(_pendingHref);

            if (card)
                _cardMetadata = _pendingMetadata ?? string.Empty;
            if (articleLink)
            {
                _activeHref = _pendingHref;
                _activeImageUrl = null;
                _activeImageAlt = null;
                _title.Clear();
            }

            if (_pendingKind == TagKind.Img && _activeHref is not null)
            {
                _activeImageUrl ??= _pendingImageUrl;
                _activeImageAlt ??= _pendingImageAlt;
            }

            if (selfClosing || IsVoid(_pendingKind))
            {
                if (articleLink)
                    EmitArticle();
                return;
            }

            EnsureCapacity();
            _frames[_frameCount++] = new Frame(_pendingHash, _pendingLength, newsList, card, articleLink);
            if (newsList)
                _newsListDepth++;
            if (card)
                _cardDepth++;
        }

        public void Text(ReadOnlySpan<byte> utf8)
        {
            if (_activeHref is null)
                return;
            Span<char> chars = stackalloc char[2];
            while (!utf8.IsEmpty)
            {
                Rune.DecodeFromUtf8(utf8, out var rune, out var consumed);
                var written = rune.EncodeToUtf16(chars);
                _title.Append(chars[..written]);
                utf8 = utf8[consumed..];
            }
        }

        public void EndTag(Utf8HtmlName name)
        {
            var hash = name.SemanticHash;
            for (var index = _frameCount - 1; index >= 0; index--)
            {
                if (_frames[index].Hash != hash || _frames[index].Length != name.Verbatim.Length)
                    continue;
                for (var popped = _frameCount - 1; popped >= index; popped--)
                    Close(_frames[popped]);
                _frameCount = index;
                return;
            }
        }

        public void EndOfFile()
        {
            for (var index = _frameCount - 1; index >= 0; index--)
                Close(_frames[index]);
            _frameCount = 0;
        }

        public List<Article> DetachResults()
        {
            var results = _results;
            _results = [];
            return results;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            ArrayPool<Frame>.Shared.Return(_frames);
            _frames = [];
        }

        private void Close(Frame frame)
        {
            if (frame.ArticleLink)
                EmitArticle();
            if (frame.Card)
            {
                _cardDepth--;
                if (_cardDepth == 0)
                    _cardMetadata = string.Empty;
            }

            if (frame.NewsList)
                _newsListDepth--;
        }

        private void EmitArticle()
        {
            if (_activeHref is null)
                return;
            AddArticle(_results, _title.ToString(), _activeHref, _activeImageUrl, _activeImageAlt, _cardMetadata);
            _activeHref = null;
            _activeImageUrl = null;
            _activeImageAlt = null;
            _title.Clear();
        }

        private void EnsureCapacity()
        {
            if (_frameCount < _frames.Length)
                return;
            var replacement = ArrayPool<Frame>.Shared.Rent(_frames.Length * 2);
            _frames.AsSpan(0, _frameCount).CopyTo(replacement);
            ArrayPool<Frame>.Shared.Return(_frames);
            _frames = replacement;
        }

        private static bool ContainsToken(ReadOnlySpan<byte> tokens, ReadOnlySpan<byte> wanted)
        {
            var index = 0;
            while (index < tokens.Length)
            {
                while (
                    index < tokens.Length
                    && tokens[index] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0C
                )
                    index++;
                var start = index;
                while (
                    index < tokens.Length
                    && tokens[index] is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0C)
                )
                    index++;
                if (tokens[start..index].SequenceEqual(wanted))
                    return true;
            }

            return false;
        }

        private static TagKind Kind(Utf8HtmlName name) =>
            name.SemanticEquals("ul"u8) ? TagKind.Ul
            : name.SemanticEquals("li"u8) ? TagKind.Li
            : name.SemanticEquals("a"u8) ? TagKind.A
            : name.SemanticEquals("img"u8) ? TagKind.Img
            : TagKind.Other;

        private static bool IsVoid(TagKind kind) => kind == TagKind.Img;

        private enum TagKind : byte
        {
            Other,
            Ul,
            Li,
            A,
            Img,
        }

        private readonly record struct Frame(ulong Hash, int Length, bool NewsList, bool Card, bool ArticleLink);
    }
}
#endif
