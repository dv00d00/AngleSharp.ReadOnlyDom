#if NET10_0

using System.IO.Pipelines;
using System.Text;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.ReadOnlyDom.Compact;
using AngleSharp.ReadOnlyDom.Compact.Document;
using AngleSharp.ReadOnlyDom.Compact.Parsing;
using AngleSharp.ReadOnlyDom.Compact.Query;
using AngleSharp.ReadOnlyDom.Filters;
using AngleSharp.ReadOnlyDom.Html;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.Text;
using BenchmarkDotNet.Attributes;
using AngleSharpDocument = AngleSharp.Dom.IDocument;
using MutableDocument = AngleSharp.Dom.Document;
using MutableElement = AngleSharp.Dom.Element;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

/// <summary>
/// Turns the README's pathological ParserBenchmark [UrlTest=qq] fixture into a realistic scraper.
/// Every lane returns the same owned objects for article links inside QQ news-list cards.
/// </summary>
[BenchmarkCategory("Extraction", "Query")]
[MemoryDiagnoser]
public class QqArticleScraperBenchmark
{
    private const int NetworkSegmentSize = 4 * 1024;
    private const string ArticleCardSelector = ".news-list li[dt-eid='em_item_article']";

    private static readonly QueryPlan<ArticleStateMachine> CallbackArticleQuery = CreateArticleQuery();
    private static readonly QueryPlan<ArticleStateMachine> CompletedElementArticleQuery = CreateCompletedArticleQuery();
    private readonly HtmlParser _angleSharp = new();
    private readonly HtmlParser _angleSharpScraper = new(CreateOptions(trackSources: false));
    private readonly HtmlParser _readOnlyNatural = new(
        new HtmlParserOptions(),
        ReadOnlyParser.CreateContext(ReadOnlyMetadataProfile.Minimal)
    );
    private readonly HtmlParser _readOnlyMinimal = CreateReadOnlyParser(ReadOnlyMetadataProfile.Minimal);
    private readonly HtmlParser _compactNatural = CompactParser.CreateParser();

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
        AssertEqual(nameof(AngleSharpUtf8Css), AngleSharpUtf8Css());
        AssertEqual(nameof(AngleSharpUtf8BodyFilteredCss), AngleSharpUtf8BodyFilteredCss());
        AssertEqual(nameof(AngleSharpUtf8QqSubtreesCss), AngleSharpUtf8QqSubtreesCss());
        AssertEqual(nameof(ReadOnlyNaturalQuery), ReadOnlyNaturalQuery());
        AssertEqual(nameof(ReadOnlyMinimalBodyFiltered), ReadOnlyMinimalBodyFiltered());
        AssertEqual(nameof(ReadOnlyUtf8BodyFiltered), ReadOnlyUtf8BodyFiltered());
        AssertEqual(nameof(ReadOnlyUtf8QqSubtrees), ReadOnlyUtf8QqSubtrees());
        AssertEqual(nameof(CompactNaturalQuery), CompactNaturalQuery());
        AssertEqual(nameof(CompactBodyFilteredResolvedIds), CompactBodyFilteredResolvedIds());
        AssertEqual(nameof(CompactUtf8BodyFilteredResolvedIds), CompactUtf8BodyFilteredResolvedIds());
        AssertEqual(nameof(CompactUtf8QqSubtreesResolvedIds), CompactUtf8QqSubtreesResolvedIds());
        AssertEqual(nameof(StreamingQueryCallbacks), StreamingQueryCallbacks());
        AssertEqual(
            nameof(StreamingQuerySegmented4KiB),
            StreamingQuerySegmented4KiB().AsTask().GetAwaiter().GetResult()
        );
        AssertEqual(nameof(StreamingQueryCompletedElements), StreamingQueryCompletedElements());

        Console.WriteLine(
            $"QQ scraper fixture {File}: {_utf8.Length:N0} UTF-8 bytes, "
                + $"{_expected.Count:N0} article-link objects."
        );
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Natural")]
    public List<Article> AngleSharpDom()
    {
        using var document = _angleSharp.ParseDocument(_html);
        return ScrapeAngleSharpCss(document);
    }

    [Benchmark]
    [BenchmarkCategory("Optimized")]
    public List<Article> AngleSharpScraperOptionsCss()
    {
        using var document = _angleSharpScraper.ParseDocument(_html);
        return ScrapeAngleSharpCss(document);
    }

    private static List<Article> ScrapeAngleSharpCss(AngleSharpDocument document)
    {
        var output = new List<Article>();
        foreach (var card in document.QuerySelectorAll(ArticleCardSelector))
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

    [Benchmark]
    [BenchmarkCategory("Optimized")]
    public List<Article> AngleSharpUtf8Css()
    {
        using var document = _angleSharpScraper.ParseDocument(
            new TextSource(new ReadOnlyByteTextSource(_utf8, Encoding.UTF8))
        );
        return ScrapeAngleSharpCss(document);
    }

    [Benchmark]
    [BenchmarkCategory("Optimized")]
    public List<Article> AngleSharpUtf8BodyFilteredCss()
    {
        var filter = new FirstTagAndAllChildren("body");
        using var document = _angleSharpScraper.ParseDocument<MutableDocument, MutableElement>(
            new TextSource(new ReadOnlyByteTextSource(_utf8, Encoding.UTF8)),
            filter.Loop
        );
        return ScrapeAngleSharpCss(document);
    }

    [Benchmark]
    [BenchmarkCategory("QqSpecificUpperBound")]
    public List<Article> AngleSharpUtf8QqSubtreesCss()
    {
        var filter = new QqNewsListTokenFilter();
        using var document = _angleSharpScraper.ParseDocument<MutableDocument, MutableElement>(
            new TextSource(new ReadOnlyByteTextSource(_utf8, Encoding.UTF8)),
            filter.Loop
        );
        return ScrapeAngleSharpCss(document);
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
    [BenchmarkCategory("Natural")]
    public List<Article> ReadOnlyNaturalQuery()
    {
        using var document = _readOnlyNatural.ParseReadOnlyDocument(_html);
        return ScrapeReadOnly(document);
    }

    [Benchmark]
    [BenchmarkCategory("Optimized")]
    public List<Article> ReadOnlyMinimalBodyFiltered()
    {
        var filter = new FirstTagAndAllChildren("body");
        using var document = _readOnlyMinimal.ParseReadOnlyDocument(_html, filter.Loop);
        return ScrapeReadOnly(document);
    }

    [Benchmark]
    [BenchmarkCategory("Optimized")]
    public List<Article> ReadOnlyUtf8BodyFiltered()
    {
        var filter = new FirstTagAndAllChildren("body");
        using var document = _readOnlyMinimal.ParseReadOnlyDocument(_utf8, Encoding.UTF8, filter.Loop);
        return ScrapeReadOnly(document);
    }

    [Benchmark]
    [BenchmarkCategory("QqSpecificUpperBound")]
    public List<Article> ReadOnlyUtf8QqSubtrees()
    {
        var filter = new QqNewsListTokenFilter();
        using var document = _readOnlyMinimal.ParseReadOnlyDocument(_utf8, Encoding.UTF8, filter.Loop);
        return ScrapeReadOnly(document);
    }

    [Benchmark]
    [BenchmarkCategory("Natural")]
    public List<Article> CompactNaturalQuery()
    {
        using var document = _compactNatural.ParseCompactDocument(_html);
        return ScrapeCompactNatural(document);
    }

    [Benchmark]
    [BenchmarkCategory("Optimized")]
    public List<Article> CompactBodyFilteredResolvedIds()
    {
        var filter = new FirstTagAndAllChildren("body");
        using var document = _compact.ParseCompactDocument(_html, filter.Loop);
        return ScrapeCompactResolvedIds(document);
    }

    [Benchmark]
    [BenchmarkCategory("Optimized")]
    public List<Article> CompactUtf8BodyFilteredResolvedIds()
    {
        var filter = new FirstTagAndAllChildren("body");
        using var document = _compact.ParseCompactDocument(_utf8, Encoding.UTF8, filter.Loop);
        return ScrapeCompactResolvedIds(document);
    }

    [Benchmark]
    [BenchmarkCategory("QqSpecificUpperBound")]
    public List<Article> CompactUtf8QqSubtreesResolvedIds()
    {
        var filter = new QqNewsListTokenFilter();
        using var document = _compact.ParseCompactDocument(_utf8, Encoding.UTF8, filter.Loop);
        return ScrapeCompactResolvedIds(document);
    }

    private static List<Article> ScrapeCompactNatural(CompactDocument document)
    {
        var output = new List<Article>();

        foreach (var list in document.Elements("ul").WithClass("news-list"))
        {
            foreach (var card in list.Elements("li").WithAttribute("dt-eid", "em_item_article"))
            {
                var metadata = card.Attr("dt-params").ToString();
                foreach (var link in card.Elements("a").WithAttribute("href"))
                {
                    var linkImage = link.Elements("img").First();
                    AddArticle(
                        output,
                        link.Text(),
                        link.Attr("href").ToString(),
                        linkImage.Exists ? linkImage.Attr("src").ToString() : null,
                        linkImage.Exists ? linkImage.Attr("alt").ToString() : null,
                        metadata
                    );
                }
            }
        }
        return output;
    }

    private static List<Article> ScrapeCompactResolvedIds(CompactDocument document)
    {
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

        var output = new List<Article>();
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
    [BenchmarkCategory("Optimized")]
    public List<Article> StreamingQueryCallbacks()
    {
        var state = CallbackArticleQuery.Execute(_utf8, new ArticleStateMachine(), Utf8InputContract.WellFormedUtf8);
        return state.DetachResults();
    }

    [Benchmark]
    [BenchmarkCategory("Optimized")]
    public async ValueTask<List<Article>> StreamingQuerySegmented4KiB()
    {
        var pipe = new Pipe(
            new PipeOptions(
                pauseWriterThreshold: 16 * 1024,
                resumeWriterThreshold: 8 * 1024,
                minimumSegmentSize: NetworkSegmentSize,
                useSynchronizationContext: false
            )
        );
        var producer = WriteSegmentsAsync(pipe.Writer, _utf8, NetworkSegmentSize);
        try
        {
            var state = await CallbackArticleQuery.ExecuteAsync(pipe.Reader, new ArticleStateMachine());
            await producer;
            return state.DetachResults();
        }
        finally
        {
            await pipe.Reader.CompleteAsync();
        }
    }

    [Benchmark]
    [BenchmarkCategory("Natural")]
    public List<Article> StreamingQueryCompletedElements()
    {
        var state = CompletedElementArticleQuery.Execute(
            _utf8,
            new ArticleStateMachine(),
            Utf8InputContract.WellFormedUtf8
        );
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
            .OnNormalizedText(static (ref state, in element) => state.CompletedLink(element), "href");

        link.Descendant("img").OnClose(static (ref state, in element) => state.CompletedImage(element), "src", "alt");
        return list.Compile();
    }

    private static QueryPlan<ArticleStateMachine> CreateArticleQuery()
    {
        var list = StreamQuery.For<ArticleStateMachine>("ul").Class("news-list");

        var card = list.Descendant("li")
            .Attribute("dt-eid", "em_item_article")
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
        var output = new List<Article>();
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
            IsNotSupportingFrames = true,
            SkipComments = true,
            SkipProcessingInstructions = true,
            SkipScriptText = true,
            SkipRawText = true,
            SkipRCDataText = true,
            SkipPlaintext = true,
            SkipCDATA = true,
            DisableElementPositionTracking = !trackSources,
            ShouldEmitAttribute = static (ref _, name) =>
                name.Span is "class" or "dt-eid" or "dt-params" or "href" or "src" or "alt",
        };

    /// <summary>
    /// Scraper-specific upper bound: the tokenizer still reads the full input, but the tree builder
    /// sees only ul.news-list subtrees. This is deliberately not a general-purpose DOM parse.
    /// </summary>
    private struct QqNewsListTokenFilter
    {
        private int _depth;

        public TokenConsumptionResult Loop(ref StructHtmlToken token, TokenConsumer next)
        {
            if (_depth == 0)
            {
                if (!IsNewsList(ref token))
                    return TokenConsumptionResult.Continue;

                _depth = 1;
                next(ref token);
                return TokenConsumptionResult.Continue;
            }

            var opensScope = OpensScope(ref token);
            var closesScope = token.Type == HtmlTokenType.EndTag;
            next(ref token);

            if (opensScope)
                _depth++;
            else if (closesScope)
                _depth--;

            return TokenConsumptionResult.Continue;
        }

        private static bool IsNewsList(ref StructHtmlToken token)
        {
            if (token.Type != HtmlTokenType.StartTag || !token.Name.Memory.Span.SequenceEqual("ul".AsSpan()))
                return false;

            for (var index = 0; index < token.Attributes.Count; index++)
            {
                var attribute = token.Attributes[index];
                if (
                    attribute.Name.Memory.Span.SequenceEqual("class".AsSpan())
                    && ContainsHtmlClass(attribute.Value.Memory.Span, "news-list")
                )
                    return true;
            }

            return false;
        }

        private static bool OpensScope(ref StructHtmlToken token)
        {
            if (token.Type != HtmlTokenType.StartTag || token.IsSelfClosing)
                return false;

            var name = token.Name.Memory.Span;
            return !(
                name
                is "area"
                    or "base"
                    or "br"
                    or "col"
                    or "embed"
                    or "hr"
                    or "img"
                    or "input"
                    or "link"
                    or "meta"
                    or "param"
                    or "source"
                    or "track"
                    or "wbr"
            );
        }

        private static bool ContainsHtmlClass(ReadOnlySpan<char> value, ReadOnlySpan<char> expected)
        {
            while (!value.IsEmpty)
            {
                var start = 0;
                while (start < value.Length && IsHtmlSpace(value[start]))
                    start++;
                value = value[start..];
                if (value.IsEmpty)
                    return false;

                var length = 0;
                while (length < value.Length && !IsHtmlSpace(value[length]))
                    length++;
                if (value[..length].SequenceEqual(expected))
                    return true;
                value = value[length..];
            }

            return false;
        }

        private static bool IsHtmlSpace(char value) => value is '\t' or '\n' or '\f' or '\r' or ' ';
    }

    private static async ValueTask WriteSegmentsAsync(PipeWriter writer, byte[] input, int segmentSize)
    {
        try
        {
            for (var offset = 0; offset < input.Length; offset += segmentSize)
            {
                var length = Math.Min(segmentSize, input.Length - offset);
                await writer.WriteAsync(input.AsMemory(offset, length));
            }
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    public sealed record Article(string Title, string Url, string? ImageUrl, string? ImageAlt, string CardMetadata);

    private sealed class ArticleStateMachine
    {
        private readonly StringBuilder _title = new();
        private List<Article> _results = [];
        private string _cardMetadata = string.Empty;
        private string? _href;
        private string? _imageUrl;
        private string? _imageAlt;

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

        public void CompletedImage(in CompletedElement element)
        {
            _imageUrl ??= element.GetAttribute("src");
            _imageAlt ??= element.GetAttribute("alt");
        }

        public void CompletedLink(in CompletedElement element)
        {
            AddArticle(
                _results,
                element.GetText(),
                element.GetAttributeOrEmpty("href"),
                _imageUrl,
                _imageAlt,
                _cardMetadata
            );
            _imageUrl = null;
            _imageAlt = null;
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
