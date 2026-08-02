using System.Text;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.ReadOnlyDom.Compact.Arena;
using AngleSharp.ReadOnlyDom.Compact.Document;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Compact.Parsing;

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
        HtmlParserOptions? parserOptions = null
    )
    {
        return CreateParserCore(
            options,
            new CompactParserHints(),
            null,
            parserOptions,
            CompactDocumentLayout.FrozenColumns
        );
    }

    internal static HtmlParser CreateParserForTesting(
        CompactMetadataOptions options = CompactMetadataOptions.None,
        CompactParserHints? hints = null,
        CompactAttributeFilter? attributeFilter = null,
        HtmlParserOptions? parserOptions = null,
        CompactDocumentLayout layout = CompactDocumentLayout.FrozenColumns
    )
    {
        return CreateParserCore(options, hints ?? new CompactParserHints(), attributeFilter, parserOptions, layout);
    }

    internal static HtmlParser CreateParser(CompactDocumentLayout layout)
    {
        return CreateParserForTesting(layout: layout);
    }

    internal static HtmlParser CreateParser(CompactMetadataOptions options, CompactDocumentLayout layout)
    {
        return CreateParserForTesting(options, layout: layout);
    }

    internal static HtmlParser CreateParser(CompactParserHints hints)
    {
        return CreateParserForTesting(hints: hints);
    }

    internal static HtmlParser CreateParser(CompactAttributeFilter attributeFilter)
    {
        return CreateParserForTesting(attributeFilter: attributeFilter);
    }

    internal static HtmlParser CreateParser(
        HtmlParserOptions parserOptions,
        CompactAttributeFilter attributeFilter
    )
    {
        return CreateParserForTesting(attributeFilter: attributeFilter, parserOptions: parserOptions);
    }

    private static HtmlParser CreateParserCore(
        CompactMetadataOptions options,
        CompactParserHints hints,
        CompactAttributeFilter? attributeFilter,
        HtmlParserOptions? parserOptions,
        CompactDocumentLayout layout
    )
    {
        var effectiveParserOptions = parserOptions ?? CreateParserOptions(options);
        if (options.HasFlag(CompactMetadataOptions.SourceLocations))
            effectiveParserOptions.IsKeepingSourceReferences = true;
        ApplyAttributeFilter(ref effectiveParserOptions, attributeFilter);
        var factory = new ArenaConstructionFactory(
            hints,
            options.HasFlag(CompactMetadataOptions.SourceLocations),
            options,
            layout
        );
        var context = BrowsingContext.New(Configuration.Default);
        return new ArenaHtmlParser(effectiveParserOptions, context, factory);
    }

    internal static CompactDocument ParseCompactDocument(
        this IHtmlParser parser,
        TextSource source,
        TokenizerMiddleware? middleware = null
    )
    {
        return Parse(parser, source, middleware);
    }

    public static CompactDocument ParseCompactDocument(
        this IHtmlParser parser,
        string source
    )
    {
        return Parse(parser, new TextSource(new StringTextSource(source)), null);
    }

    internal static CompactDocument ParseCompactDocument(
        this IHtmlParser parser,
        string source,
        TokenizerMiddleware? middleware
    )
    {
        return Parse(parser, new TextSource(new StringTextSource(source)), middleware);
    }

    public static CompactDocument ParseCompactDocument(
        this IHtmlParser parser,
        ReadOnlyMemory<char> source
    )
    {
        return Parse(parser, new TextSource(new ReadOnlyMemoryTextSource(source)), null);
    }

    internal static CompactDocument ParseCompactDocument(
        this IHtmlParser parser,
        ReadOnlyMemory<char> source,
        TokenizerMiddleware? middleware
    )
    {
        return Parse(parser, new TextSource(new ReadOnlyMemoryTextSource(source)), middleware);
    }

    internal static CompactDocument ParseCompactDocument(
        this IHtmlParser parser,
        char[] source,
        int length,
        TokenizerMiddleware? middleware = null
    )
    {
        return Parse(parser, new TextSource(new CharArrayTextSource(source, length)), middleware);
    }

    public static CompactDocument ParseCompactDocument(
        this IHtmlParser parser,
        ReadOnlyMemory<byte> source,
        Encoding? encoding = null
    )
    {
        return ParseCompactDocument(parser, source, encoding, null);
    }

    internal static CompactDocument ParseCompactDocument(
        this IHtmlParser parser,
        ReadOnlyMemory<byte> source,
        Encoding? encoding,
        TokenizerMiddleware? middleware
    )
    {
        return Parse(
            parser,
            new TextSource(
                encoding is null ? new ReadOnlyByteTextSource(source) : new ReadOnlyByteTextSource(source, encoding)
            ),
            middleware
        );
    }

    public static Task<CompactDocument> ParseCompactDocumentAsync(
        this HtmlParser parser,
        Stream source,
        HtmlStreamSourceMode sourceMode = HtmlStreamSourceMode.Streaming,
        Encoding? encoding = null,
        CancellationToken cancel = default
    )
    {
        return ParseCompactDocumentAsync(parser, source, sourceMode, encoding, null, cancel);
    }

    internal static async Task<CompactDocument> ParseCompactDocumentAsync(
        this HtmlParser parser,
        Stream source,
        HtmlStreamSourceMode sourceMode,
        Encoding? encoding,
        TokenizerMiddleware? middleware,
        CancellationToken cancel
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

    internal static HtmlParserOptions CreateParserOptions(CompactMetadataOptions options)
    {
        return new HtmlParserOptions
        {
            SkipComments = true,
            SkipProcessingInstructions = true,
            IsKeepingSourceReferences = options.HasFlag(CompactMetadataOptions.SourceLocations)
        };
    }

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
    ///     Compact parsing runs on the arena's handle-based construction backend, which only
    ///     <see cref="CreateParser" /> can wire up: AngleSharp hands the backend to the tree builder as an
    ///     argument rather than resolving it from the browsing context, and <see cref="HtmlParser" /> does
    ///     not expose its context.
    /// </summary>
    private static IArenaHtmlParser RequireArenaParser(IHtmlParser parser)
    {
        return parser as IArenaHtmlParser
               ?? throw new InvalidOperationException(
                   $"Compact documents require a parser created by {nameof(CompactParser)}.{nameof(CreateParser)}(), "
                   + $"but received a {parser.GetType().Name}."
               );
    }

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

        public ArenaHtmlParser(HtmlParserOptions options, IBrowsingContext context, ArenaConstructionFactory factory)
            : base(options, context)
        {
            _factory = factory;
        }

        public ArenaDocument CreateArenaDocument(TextSource source)
        {
            return _factory.CreateDocument(source);
        }

        public ArenaDocument ParseArenaDocument(TextSource source, TokenizerMiddleware? middleware)
        {
            return ParseDocument(source, _factory, middleware);
        }

        public Task<ArenaDocument> ParseArenaDocumentAsync(
            Stream source,
            HtmlStreamSourceMode sourceMode,
            Encoding? encoding,
            TokenizerMiddleware? middleware,
            CancellationToken cancel
        )
        {
            return ParseDocumentAsync(
                source,
                sourceMode,
                _factory,
                encoding,
                middleware,
                cancel
            );
        }
    }
}
