using System.Text;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.ReadOnlyDom.Compact.Arena;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Compact;

public static class CompactParser
{
    private static readonly Func<IBrowsingContext, ArenaConstructionFactory> Service =
        _ => new ArenaConstructionFactory(
            new CompactParserHints(),
            trackSourceReferences: false,
            CompactMetadataOptions.None,
            CompactDocumentLayout.FrozenColumns
        );

    public static readonly IConfiguration DefaultConfig = Configuration.Default.With(Service);
    public static readonly IBrowsingContext DefaultContext = BrowsingContext.New(DefaultConfig);
    private static readonly IBrowsingContext FrozenParentContext = CreateContextCore(
        CompactMetadataOptions.ParentLinks,
        CompactDocumentLayout.FrozenColumns
    );
    private static readonly IBrowsingContext FrozenSourceContext = CreateContextCore(
        CompactMetadataOptions.SourceLocations,
        CompactDocumentLayout.FrozenColumns
    );
    private static readonly IBrowsingContext FrozenParentSourceContext = CreateContextCore(
        CompactMetadataOptions.ParentLinks | CompactMetadataOptions.SourceLocations,
        CompactDocumentLayout.FrozenColumns
    );
    private static readonly IBrowsingContext PackedContext = CreateContextCore(
        CompactMetadataOptions.None,
        CompactDocumentLayout.Packed
    );
    private static readonly IBrowsingContext PackedParentContext = CreateContextCore(
        CompactMetadataOptions.ParentLinks,
        CompactDocumentLayout.Packed
    );
    private static readonly IBrowsingContext PackedSourceContext = CreateContextCore(
        CompactMetadataOptions.SourceLocations,
        CompactDocumentLayout.Packed
    );
    private static readonly IBrowsingContext PackedParentSourceContext = CreateContextCore(
        CompactMetadataOptions.ParentLinks | CompactMetadataOptions.SourceLocations,
        CompactDocumentLayout.Packed
    );

    public static IBrowsingContext CreateContext(
        CompactMetadataOptions options = CompactMetadataOptions.None,
        CompactParserHints? hints = null,
        CompactDocumentLayout layout = CompactDocumentLayout.FrozenColumns
    )
    {
        if (hints is not null)
            return CreateContextCore(options, layout, hints);

        return (options, layout) switch
        {
            (CompactMetadataOptions.None, CompactDocumentLayout.FrozenColumns) => DefaultContext,
            (CompactMetadataOptions.ParentLinks, CompactDocumentLayout.FrozenColumns) => FrozenParentContext,
            (CompactMetadataOptions.SourceLocations, CompactDocumentLayout.FrozenColumns) => FrozenSourceContext,
            (
                CompactMetadataOptions.ParentLinks | CompactMetadataOptions.SourceLocations,
                CompactDocumentLayout.FrozenColumns
            ) => FrozenParentSourceContext,
            (CompactMetadataOptions.None, CompactDocumentLayout.Packed) => PackedContext,
            (CompactMetadataOptions.ParentLinks, CompactDocumentLayout.Packed) => PackedParentContext,
            (CompactMetadataOptions.SourceLocations, CompactDocumentLayout.Packed) => PackedSourceContext,
            (
                CompactMetadataOptions.ParentLinks | CompactMetadataOptions.SourceLocations,
                CompactDocumentLayout.Packed
            ) => PackedParentSourceContext,
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
    }

    private static IBrowsingContext CreateContextCore(
        CompactMetadataOptions options,
        CompactDocumentLayout layout,
        CompactParserHints? hints = null
    )
    {
        var effectiveHints = hints ?? new CompactParserHints();
        var trackSourceReferences = options.HasFlag(CompactMetadataOptions.SourceLocations);
        var configuration = Configuration.Default.With(_ => new ArenaConstructionFactory(
            effectiveHints,
            trackSourceReferences,
            options,
            layout
        ));
        return BrowsingContext.New(configuration);
    }

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
        return new HtmlParser(effectiveParserOptions, CreateContext(options, hints, layout));
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
        var document = await parser
            .ParseDocumentAsync<ArenaDocument, ArenaElement>(source, sourceMode, encoding, middleware, cancel)
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

    private static CompactDocument Parse(IHtmlParser parser, TextSource source, TokenizerMiddleware? middleware)
    {
        var document = parser.ParseDocument<ArenaDocument, ArenaElement>(source, middleware);
        try
        {
            return document.CreateCompactDocument();
        }
        finally
        {
            document.Dispose();
        }
    }
}
