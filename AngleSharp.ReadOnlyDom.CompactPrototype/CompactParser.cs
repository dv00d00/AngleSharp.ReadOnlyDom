using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.ReadOnlyDom.CompactPrototype.Arena;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.CompactPrototype;

public static class CompactParser
{
    public static CompactDocument Parse(
        string html,
        CompactMetadataOptions options = CompactMetadataOptions.None,
        CompactParserHints? hints = null,
        CompactAttributeFilter? attributeFilter = null,
        HtmlParserOptions? parserOptions = null,
        TokenizerMiddleware? middleware = null
    ) => Parse(new TextSource(new StringTextSource(html)), options, hints, attributeFilter, parserOptions, middleware);

    public static CompactDocument Parse(
        ReadOnlyMemory<char> html,
        CompactMetadataOptions options = CompactMetadataOptions.None,
        CompactParserHints? hints = null,
        CompactAttributeFilter? attributeFilter = null,
        HtmlParserOptions? parserOptions = null,
        TokenizerMiddleware? middleware = null
    ) =>
        Parse(
            new TextSource(new ReadOnlyMemoryTextSource(html)),
            options,
            hints,
            attributeFilter,
            parserOptions,
            middleware
        );

    public static CompactDocument Parse(
        char[] html,
        int length,
        CompactMetadataOptions options = CompactMetadataOptions.None,
        CompactParserHints? hints = null,
        CompactAttributeFilter? attributeFilter = null,
        HtmlParserOptions? parserOptions = null,
        TokenizerMiddleware? middleware = null
    ) =>
        Parse(
            new TextSource(new CharArrayTextSource(html, length)),
            options,
            hints,
            attributeFilter,
            parserOptions,
            middleware
        );

    private static CompactDocument Parse(
        TextSource source,
        CompactMetadataOptions options,
        CompactParserHints? hints,
        CompactAttributeFilter? attributeFilter,
        HtmlParserOptions? parserOptions,
        TokenizerMiddleware? middleware
    )
    {
        hints ??= new CompactParserHints();
        var effectiveParserOptions = parserOptions ?? CreateParserOptions(options);
        ApplyAttributeFilter(ref effectiveParserOptions, attributeFilter);
        var configuration = Configuration.Default.With(_ => new ArenaConstructionFactory(
            hints,
            effectiveParserOptions.IsKeepingSourceReferences
        ));
        var context = BrowsingContext.New(configuration);
        var parser = new HtmlParser(effectiveParserOptions, context);
        var document = parser.ParseDocument<ArenaDocument, ArenaElement>(source, middleware);
        try
        {
            return document.Arena.Finalize(document.NodeHandle, options);
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
}
