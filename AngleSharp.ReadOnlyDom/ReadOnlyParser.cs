using System.Runtime.CompilerServices;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom.Events;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Html;
using AngleSharp.ReadOnlyDom.Html.Model;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom;

public static class ReadOnlyParser
{
    private static readonly ConditionalWeakTable<HtmlParser, ParserProfile> ParserProfiles = new();
    private static readonly Func<IBrowsingContext, ReadOnlyDomConstructionFactory> _service =
        _ => new ReadOnlyDomConstructionFactory(ReadOnlyMetadataProfile.Minimal);

    public static readonly IConfiguration DefaultConfig = Configuration.Default.With(_service);
    public static readonly IBrowsingContext DefaultContext = BrowsingContext.New(DefaultConfig);

    private static readonly IBrowsingContext NavigableContext = CreateContextCore(ReadOnlyMetadataProfile.Navigable);
    private static readonly IBrowsingContext SourceMappedContext = CreateContextCore(
        ReadOnlyMetadataProfile.SourceMapped
    );
    private static readonly IBrowsingContext DiagnosticContext = CreateContextCore(ReadOnlyMetadataProfile.Diagnostic);

    public static IBrowsingContext CreateContext(ReadOnlyMetadataProfile profile) =>
        profile switch
        {
            ReadOnlyMetadataProfile.Minimal => DefaultContext,
            ReadOnlyMetadataProfile.Navigable => NavigableContext,
            ReadOnlyMetadataProfile.SourceMapped => SourceMappedContext,
            ReadOnlyMetadataProfile.Diagnostic => DiagnosticContext,
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };

    private static IBrowsingContext CreateContextCore(ReadOnlyMetadataProfile profile)
    {
        var configuration = Configuration.Default.With(_ => new ReadOnlyDomConstructionFactory(profile));
        return BrowsingContext.New(configuration);
    }

    public static HtmlParser CreateParser(ReadOnlyMetadataProfile profile)
    {
        var parser = new HtmlParser(profile.ParserOptions(), CreateContext(profile));
        ParserProfiles.Add(parser, new ParserProfile(profile));
        return parser;
    }

    public static IReadOnlyDocument ParseReadOnlyDocument(
        this IHtmlParser parser,
        TextSource source,
        TokenizerMiddleware? middleware = null
    )
    {
        return ParseWithDiagnostics(
            parser,
            () => parser.ParseDocument<ReadOnlyDocument, ReadOnlyHtmlElement>(source, middleware)
        );
    }

    public static IReadOnlyDocument ParseReadOnlyDocument(
        this IHtmlParser parser,
        char[] source,
        int length,
        TokenizerMiddleware? middleware = null
    )
    {
        return ParseWithDiagnostics(
            parser,
            () =>
                parser.ParseDocument<ReadOnlyDocument, ReadOnlyHtmlElement>(
                    new TextSource(new CharArrayTextSource(source, length)),
                    middleware
                )
        );
    }

    public static IReadOnlyDocument ParseReadOnlyDocument(
        this IHtmlParser parser,
        string source,
        TokenizerMiddleware? middleware = null
    )
    {
        return ParseWithDiagnostics(
            parser,
            () =>
                parser.ParseDocument<ReadOnlyDocument, ReadOnlyHtmlElement>(
                    new TextSource(new StringTextSource(source)),
                    middleware
                )
        );
    }

    public static IReadOnlyDocument ParseReadOnlyDocument(
        this IHtmlParser parser,
        ReadOnlyMemory<char> source,
        TokenizerMiddleware? middleware = null
    )
    {
        return ParseWithDiagnostics(
            parser,
            () =>
                parser.ParseDocument<ReadOnlyDocument, ReadOnlyHtmlElement>(
                    new TextSource(new ReadOnlyMemoryTextSource(source)),
                    middleware
                )
        );
    }

#if NET8_0_OR_GREATER
    public static IReadOnlyDocument ParseReadOnlyDocument(
        this IHtmlParser parser,
        ReadOnlyMemory<byte> source,
        Encoding? encoding = null,
        TokenizerMiddleware? middleware = null
    ) =>
        ParseReadOnlyDocument(
            parser,
            new TextSource(
                encoding is null
                    ? new ReadOnlyByteTextSource(source)
                    : new ReadOnlyByteTextSource(source, encoding)
            ),
            middleware
        );
#endif

    private static IReadOnlyDocument ParseWithDiagnostics(IHtmlParser parser, Func<ReadOnlyDocument> parse)
    {
        if (
            parser is not HtmlParser htmlParser
            || !ParserProfiles.TryGetValue(htmlParser, out var marker)
            || !marker.Profile.Features().HasFlag(MetadataFeatures.Diagnostics)
        )
        {
            return parse();
        }

        List<Exception>? errors = null;
        DomEventHandler handler = (_, @event) =>
        {
            if (@event is HtmlErrorEvent error)
            {
                errors ??= [];
                errors.Add(new HtmlParseException(error.Code, error.Message, error.Position));
            }
        };
        htmlParser.Error += handler;
        try
        {
            var document = parse();
            if (errors is not null)
            {
                foreach (var error in errors)
                    document.TrackError(error);
            }

            return document;
        }
        finally
        {
            htmlParser.Error -= handler;
        }
    }

    private sealed class ParserProfile
    {
        public ParserProfile(ReadOnlyMetadataProfile profile)
        {
            Profile = profile;
        }

        public ReadOnlyMetadataProfile Profile { get; }
    }
}
