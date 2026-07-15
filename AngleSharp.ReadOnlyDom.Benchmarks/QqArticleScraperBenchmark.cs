#if NET10_0
using System.Buffers;
using System.Text;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.ReadOnlyDom.Compact;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.ReadOnlyDom.Compact.Experimental;
using AngleSharp.ReadOnlyDom.Filters;
using AngleSharp.ReadOnlyDom.Html;
using BenchmarkDotNet.Attributes;

using Element = AngleSharp.ReadOnlyDom.Streaming.Element;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

/// <summary>
/// Turns the README's pathological ParserBenchmark [UrlTest=qq] fixture into a realistic scraper.
/// Every lane returns the same owned objects for article links inside QQ news-list cards.
/// </summary>
[MemoryDiagnoser]
public class QqArticleScraperBenchmark
{
    private static readonly QueryPlan<CompiledArticleState> ArticleQuery =
        CreateArticleQuery();
    private readonly HtmlParser _angleSharp = new();
    private readonly HtmlParser _readOnlyMinimal = CreateReadOnlyParser(ReadOnlyMetadataProfile.Minimal);
    private readonly HtmlParser _readOnlySourceMapped = CreateReadOnlyParser(ReadOnlyMetadataProfile.SourceMapped);
    private readonly HtmlParser _compact = CompactParser.CreateParser(parserOptions: CreateOptions(trackSources: false));
    private string _html = null!;
    private byte[] _utf8 = null!;
    private List<Article> _expected = null!;

    [GlobalSetup]
    public void Setup()
    {
        _html = BenchmarkCorpus.Load("full").Single(static document => document.Name == "qq").Html;
        _utf8 = Encoding.UTF8.GetBytes(_html);
        _expected = AngleSharpDom();

        AssertEqual(nameof(ReadOnlyMinimalFull), ReadOnlyMinimalFull());
        AssertEqual(nameof(ReadOnlyMinimalBodyFiltered), ReadOnlyMinimalBodyFiltered());
        AssertEqual(nameof(ReadOnlySourceMappedBodyFiltered), ReadOnlySourceMappedBodyFiltered());
        AssertEqual(nameof(CompactFrozenBodyFiltered), CompactFrozenBodyFiltered());
        AssertEqual(nameof(NativeUtf8Fold), NativeUtf8Fold());
        AssertEqual(nameof(QueryCompiledUtf8Fold), QueryCompiledUtf8Fold());

        Console.WriteLine(
            $"QQ scraper fixture: {_utf8.Length:N0} UTF-8 bytes, {_expected.Count:N0} article-link objects."
        );
    }

    [Benchmark(Baseline = true)]
    public List<Article> AngleSharpDom()
    {
        using var document = _angleSharp.ParseDocument(_html);
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

    private static QueryPlan<CompiledArticleState> CreateArticleQuery()
    {
        var list = QueryNode<CompiledArticleState>.Root(
            Selector.Tag("ul").WithClass("news-list")
        );
        var card = list.Descendant(
                Selector.Tag("li").WithAttribute("dt-eid", "em_item_article")
            )
            .OnStart(
                static (ref CompiledArticleState state, in Element element) =>
                    state.StartCard(element),
                "dt-params"
            )
            .OnEnd(static (ref CompiledArticleState state) => state.EndCard());
        var link = card.Descendant(Selector.Tag("a").WithAttribute("href"))
            .OnStart(
                static (ref CompiledArticleState state, in Element element) =>
                    state.StartLink(element),
                "href"
            )
            .OnText(static (ref CompiledArticleState state, ReadOnlySpan<byte> text) =>
                state.AppendText(text))
            .OnEnd(static (ref CompiledArticleState state) => state.EndLink());
        link.Descendant(Selector.Tag("img"))
            .OnStart(
                static (ref CompiledArticleState state, in Element element) =>
                    state.Image(element),
                "src",
                "alt"
            );
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

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static HtmlParser CreateReadOnlyParser(ReadOnlyMetadataProfile profile) =>
        new(CreateOptions(profile is ReadOnlyMetadataProfile.SourceMapped or ReadOnlyMetadataProfile.Diagnostic), ReadOnlyParser.CreateContext(profile));

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
            ShouldEmitAttribute = static (ref StructHtmlToken _, ReadOnlyMemory<char> name) =>
                name.Span is "class" or "dt-eid" or "dt-params" or "href" or "src" or "alt",
        };

    public sealed record Article(
        string Title,
        string Url,
        string? ImageUrl,
        string? ImageAlt,
        string CardMetadata
    );

    private sealed class CompiledArticleState
    {
        private readonly StringBuilder _title = new();
        private List<Article> _results = new(128);
        private string _cardMetadata = string.Empty;
        private string? _href;
        private string? _imageUrl;
        private string? _imageAlt;

        public void StartCard(in Element element) =>
            _cardMetadata = Decode(element, "dt-params") ?? string.Empty;

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

        private static string? Decode(in Element element, string attribute) =>
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

        public void StartTag(ReadOnlySpan<byte> name)
        {
            _pendingKind = Kind(name);
            _pendingHash = HashAscii(name);
            _pendingLength = name.Length;
            _pendingNewsList = false;
            _pendingCard = false;
            _pendingMetadata = null;
            _pendingHref = null;
            _pendingImageUrl = null;
            _pendingImageAlt = null;
        }

        public void Attribute(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {
            switch (_pendingKind)
            {
                case TagKind.Ul when name.SequenceEqual("class"u8):
                    _pendingNewsList = ContainsToken(value, "news-list"u8);
                    break;
                case TagKind.Li when _newsListDepth != 0 && name.SequenceEqual("dt-eid"u8):
                    _pendingCard = value.SequenceEqual("em_item_article"u8);
                    break;
                case TagKind.Li when _newsListDepth != 0 && name.SequenceEqual("dt-params"u8):
                    _pendingMetadata = Encoding.UTF8.GetString(value);
                    break;
                case TagKind.A when _cardDepth != 0 && name.SequenceEqual("href"u8):
                    _pendingHref = Encoding.UTF8.GetString(value);
                    break;
                case TagKind.Img when _activeHref is not null && name.SequenceEqual("src"u8):
                    _pendingImageUrl = Encoding.UTF8.GetString(value);
                    break;
                case TagKind.Img when _activeHref is not null && name.SequenceEqual("alt"u8):
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
            _frames[_frameCount++] = new Frame(
                _pendingHash,
                _pendingLength,
                newsList,
                card,
                articleLink
            );
            if (newsList) _newsListDepth++;
            if (card) _cardDepth++;
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

        public void EndTag(ReadOnlySpan<byte> name)
        {
            var hash = HashAscii(name);
            for (var index = _frameCount - 1; index >= 0; index--)
            {
                if (_frames[index].Hash != hash || _frames[index].Length != name.Length)
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
            if (_disposed) return;
            _disposed = true;
            ArrayPool<Frame>.Shared.Return(_frames);
            _frames = [];
        }

        private void Close(Frame frame)
        {
            if (frame.ArticleLink) EmitArticle();
            if (frame.Card)
            {
                _cardDepth--;
                if (_cardDepth == 0) _cardMetadata = string.Empty;
            }
            if (frame.NewsList) _newsListDepth--;
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
            if (_frameCount < _frames.Length) return;
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
                while (index < tokens.Length && tokens[index] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0C) index++;
                var start = index;
                while (index < tokens.Length && tokens[index] is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0C)) index++;
                if (tokens[start..index].SequenceEqual(wanted)) return true;
            }
            return false;
        }

        private static TagKind Kind(ReadOnlySpan<byte> name) =>
            name.SequenceEqual("ul"u8) ? TagKind.Ul
                : name.SequenceEqual("li"u8) ? TagKind.Li
                : name.SequenceEqual("a"u8) ? TagKind.A
                : name.SequenceEqual("img"u8) ? TagKind.Img
                : TagKind.Other;

        private static bool IsVoid(TagKind kind) => kind == TagKind.Img;

        private static ulong HashAscii(ReadOnlySpan<byte> value)
        {
            var hash = Utf8DivFingerprintFold.OffsetBasis;
            foreach (var character in value)
            {
                hash ^= character;
                hash *= Utf8DivFingerprintFold.Prime;
            }
            return hash;
        }

        private enum TagKind : byte { Other, Ul, Li, A, Img }
        private readonly record struct Frame(ulong Hash, int Length, bool NewsList, bool Card, bool ArticleLink);
    }
}
#endif
