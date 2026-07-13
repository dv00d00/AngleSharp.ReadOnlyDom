using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Compact.Arena;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Compact;

public sealed class CompactParserSession
{
    private readonly HtmlParser _parser;
    private readonly CompactMetadataOptions _options;
    private readonly CompactDocumentLayout _layout;

    public CompactParserSession(
        CompactMetadataOptions options = CompactMetadataOptions.None,
        CompactParserHints? hints = null,
        CompactAttributeFilter? attributeFilter = null,
        HtmlParserOptions? parserOptions = null,
        CompactDocumentLayout layout = CompactDocumentLayout.FrozenColumns
    )
    {
        _options = options;
        _layout = layout;
        hints ??= new CompactParserHints();
        var effectiveParserOptions = parserOptions ?? CompactParser.CreateParserOptions(options);
        CompactParser.ApplyAttributeFilter(ref effectiveParserOptions, attributeFilter);
        var configuration = Configuration.Default.With(_ => new ArenaConstructionFactory(
            hints,
            effectiveParserOptions.IsKeepingSourceReferences
        ));
        var context = BrowsingContext.New(configuration);
        _parser = new HtmlParser(effectiveParserOptions, context);
    }

    public CompactDocument Parse(string html, TokenizerMiddleware? middleware = null) =>
        Parse(new TextSource(new StringTextSource(html)), middleware);

    public CompactDocument Parse(ReadOnlyMemory<char> html, TokenizerMiddleware? middleware = null) =>
        Parse(new TextSource(new ReadOnlyMemoryTextSource(html)), middleware);

    public CompactDocument Parse(char[] html, int length, TokenizerMiddleware? middleware = null) =>
        Parse(new TextSource(new CharArrayTextSource(html, length)), middleware);

    private CompactDocument Parse(TextSource source, TokenizerMiddleware? middleware)
    {
        var document = _parser.ParseDocument<ArenaDocument, ArenaElement>(source, middleware);
        try
        {
            return document.CreateCompactDocument(_options, _layout);
        }
        finally
        {
            document.Dispose();
        }
    }
}
