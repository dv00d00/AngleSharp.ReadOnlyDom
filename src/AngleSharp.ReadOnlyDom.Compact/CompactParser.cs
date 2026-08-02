using System.Text;
using AngleSharp.Html.Construction;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.ReadOnlyDom.Compact.Arena;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Compact;

internal interface IArenaHtmlParser
{
    ArenaDocument CreateArenaDocument(TextSource source);

    ArenaDocument ParseArenaDocument(TextSource source, TokenizerMiddleware? middleware = null);

    Task<ArenaDocument> ParseArenaDocumentAsync(
        Stream source,
        HtmlStreamSourceMode sourceMode,
        Encoding? encoding,
        TokenizerMiddleware? middleware,
        CancellationToken cancel
    );
}

public static class CompactParser
{
    public static HtmlParser CreateParser(
        CompactMetadataOptions options = CompactMetadataOptions.None,
        CompactParserHints? hints = null,
        CompactAttributeFilter? attributeFilter = null,
        HtmlParserOptions? parserOptions = null,
        CompactDocumentLayout layout = CompactDocumentLayout.FrozenColumns
    )
    {
        var effectiveParserOptions = parserOptions ?? CreateParserOptions(options);
        if (options.HasFlag(CompactMetadataOptions.SourceLocations))
            effectiveParserOptions.IsKeepingSourceReferences = true;
        ApplyAttributeFilter(ref effectiveParserOptions, attributeFilter);
        var factory = new ArenaConstructionFactory(
            hints ?? new CompactParserHints(),
            options.HasFlag(CompactMetadataOptions.SourceLocations),
            options,
            layout
        );
        var context = BrowsingContext.New(Configuration.Default);
        return new ArenaHtmlParser(effectiveParserOptions, context, factory);
    }

    public static CompactDocument ParseCompactDocument(
        this IHtmlParser parser,
        TextSource source,
        TokenizerMiddleware? middleware = null
    ) => Parse(parser, source, middleware);

    public static CompactDocument ParseCompactDocument(
        this IHtmlParser parser,
        string source,
        TokenizerMiddleware? middleware = null
    ) => Parse(parser, new TextSource(new StringTextSource(source)), middleware);

    public static CompactDocument ParseCompactDocument(
        this IHtmlParser parser,
        ReadOnlyMemory<char> source,
        TokenizerMiddleware? middleware = null
    ) => Parse(parser, new TextSource(new ReadOnlyMemoryTextSource(source)), middleware);

    public static CompactDocument ParseCompactDocument(
        this IHtmlParser parser,
        char[] source,
        int length,
        TokenizerMiddleware? middleware = null
    ) => Parse(parser, new TextSource(new CharArrayTextSource(source, length)), middleware);

    public static CompactDocument ParseCompactDocument(
        this IHtmlParser parser,
        ReadOnlyMemory<byte> source,
        Encoding? encoding = null,
        TokenizerMiddleware? middleware = null
    ) =>
        Parse(
            parser,
            new TextSource(
                encoding is null ? new ReadOnlyByteTextSource(source) : new ReadOnlyByteTextSource(source, encoding)
            ),
            middleware
        );

    public static async Task<CompactDocument> ParseCompactDocumentAsync(
        this HtmlParser parser,
        Stream source,
        HtmlStreamSourceMode sourceMode = HtmlStreamSourceMode.Streaming,
        Encoding? encoding = null,
        TokenizerMiddleware? middleware = null,
        CancellationToken cancel = default
    )
    {
        var document = await RequireArenaParser(parser)
            .ParseArenaDocumentAsync(source, sourceMode, encoding, middleware, cancel)
            .ConfigureAwait(false);
        try
        {
            return document.CreateCompactDocument();
        }
        finally
        {
            document.Dispose();
        }
    }

    internal static HtmlParserOptions CreateParserOptions(CompactMetadataOptions options) =>
        new()
        {
            SkipComments = true,
            SkipProcessingInstructions = true,
            IsKeepingSourceReferences = options.HasFlag(CompactMetadataOptions.SourceLocations),
        };

    internal static void ApplyAttributeFilter(
        ref HtmlParserOptions parserOptions,
        CompactAttributeFilter? attributeFilter
    )
    {
        if (attributeFilter is not null)
            parserOptions.ShouldEmitAttribute = (ref StructHtmlToken token, ReadOnlyMemory<char> name) =>
                attributeFilter(ref token, name);
    }

    /// <summary>
    /// Compact parsing runs on the arena's handle-based construction backend, which only
    /// <see cref="CreateParser"/> can wire up: AngleSharp hands the backend to the tree builder as an
    /// argument rather than resolving it from the browsing context, and <see cref="HtmlParser"/> does
    /// not expose its context.
    /// </summary>
    private static IArenaHtmlParser RequireArenaParser(IHtmlParser parser) =>
        parser as IArenaHtmlParser
        ?? throw new InvalidOperationException(
            $"Compact documents require a parser created by {nameof(CompactParser)}.{nameof(CreateParser)}(), "
                + $"but received a {parser.GetType().Name}."
        );

    private static CompactDocument Parse(IHtmlParser parser, TextSource source, TokenizerMiddleware? middleware)
    {
        var document = RequireArenaParser(parser).ParseArenaDocument(source, middleware);
        try
        {
            return document.CreateCompactDocument();
        }
        finally
        {
            document.Dispose();
        }
    }

    private sealed class ArenaHtmlParser : HtmlParser, IArenaHtmlParser
    {
        private readonly ArenaConstructionFactory _factory;

        public ArenaHtmlParser(
            HtmlParserOptions options,
            IBrowsingContext context,
            ArenaConstructionFactory factory
        )
            : base(options, context)
        {
            _factory = factory;
        }

        public ArenaDocument CreateArenaDocument(TextSource source) =>
            ((IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>)_factory).CreateDocument(source);

        public ArenaDocument ParseArenaDocument(TextSource source, TokenizerMiddleware? middleware) =>
            ParseDocument(
                source,
                (IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>)_factory,
                middleware
            );

        public Task<ArenaDocument> ParseArenaDocumentAsync(
            Stream source,
            HtmlStreamSourceMode sourceMode,
            Encoding? encoding,
            TokenizerMiddleware? middleware,
            CancellationToken cancel
        ) => ParseDocumentAsync(
            source,
            sourceMode,
            (IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>)_factory,
            encoding,
            middleware,
            cancel
        );
    }
}
