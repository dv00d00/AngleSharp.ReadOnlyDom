using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.CompactPrototype.Arena;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.CompactPrototype;

public sealed class CompactParserSession
{
    private readonly HtmlParser _parser;
    private readonly CompactMetadataOptions _options;

    public CompactParserSession(
        CompactMetadataOptions options = CompactMetadataOptions.None,
        CompactParserHints? hints = null,
        CompactAttributeFilter? attributeFilter = null,
        HtmlParserOptions? parserOptions = null
    )
    {
        _options = options;
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
            return document.Arena.Finalize(document.NodeHandle, _options);
        }
        finally
        {
            document.Dispose();
        }
    }
}
