using System.Text;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.ReadOnlyDom.Compact.Arena;
using AngleSharp.ReadOnlyDom.Compact.Document;
using AngleSharp.ReadOnlyDom.Compact.Parsing;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Compact.Projection;

public sealed partial class CompactProjectionPlan
{
    private readonly IBrowsingContext _context;
    private readonly ArenaConstructionFactory _factory;
    private readonly CompactProjectionFieldDefinition[] _fields;
    private readonly HtmlParserOptions _parserOptions;
    private readonly string[] _retainedAttributes;
    private readonly CompactProjectionSelector _scope;

    internal CompactProjectionPlan(
        CompactProjectionSelector scope,
        CompactProjectionCardinality cardinality,
        CompactProjectionFieldDefinition[] fields
    )
    {
        _scope = scope;
        Cardinality = cardinality;
        _fields = fields;
        Requirements = BuildRequirements(scope, fields);
        _retainedAttributes = [.. Requirements.RetainedAttributes];

        _factory = new ArenaConstructionFactory(
            new CompactParserHints(),
            false,
            CompactMetadataOptions.None,
            CompactDocumentLayout.FrozenColumns,
            new CompactProjectionDefinition(this)
        );
        _context = BrowsingContext.New(Configuration.Default.With(_ => _factory));
        _parserOptions = CompactParser.CreateParserOptions(CompactMetadataOptions.None);
        _parserOptions.ShouldEmitAttribute = ShouldRetainAttribute;
    }

    internal CompactProjectionCardinality Cardinality { get; }
    internal CompactProjectionRequirements Requirements { get; }

    public CompactProjectionResult Execute(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var parser = new HtmlParser(_parserOptions, _context);
        var textSource = new TextSource(new StringTextSource(source));
        var tokensProcessed = 0;
        TokenizerMiddleware middleware = (ref StructHtmlToken token, TokenConsumer next) =>
        {
            tokensProcessed++;
            next(ref token);
            return TokenConsumptionResult.Continue;
        };
        var document = parser.ParseDocument(
            textSource,
            _factory,
            middleware
        );
        try
        {
            document.SetTokensProcessed(tokensProcessed);
            var consumed = Math.Min(source.Length, document.Source.Index);
            return document.CreateProjectionResult(Encoding.UTF8.GetByteCount(source.AsSpan(0, consumed)));
        }
        finally
        {
            document.Dispose();
        }
    }

    private bool ShouldRetainAttribute(ref StructHtmlToken token, ReadOnlyMemory<char> name)
    {
        var attribute = name.Span;
        foreach (var retained in _retainedAttributes)
            if (attribute.Equals(retained, StringComparison.OrdinalIgnoreCase))
                return true;
        return CompactConstructionAttributePolicy.IsRequiredByTreeBuilder(ref token, attribute);
    }

    private static CompactProjectionRequirements BuildRequirements(
        CompactProjectionSelector scope,
        CompactProjectionFieldDefinition[] fields
    )
    {
        var inspected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddSelector(scope);
        foreach (var field in fields)
        {
            var projection = field.Projection;
            if (projection.Selector is not null)
                AddSelector(projection.Selector);
            if (projection.Attribute is not null)
                retained.Add(projection.Attribute);
        }

        retained.UnionWith(inspected);
        return new CompactProjectionRequirements(
            inspected.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            retained.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            fields.Any(static field => field.Projection.Kind != CompactFieldProjectionKind.Attribute)
        );

        void AddSelector(CompactProjectionSelector selector)
        {
            foreach (var step in selector.Steps)
            {
                if (step.Id is not null)
                    inspected.Add("id");
                if (step.ClassToken is not null)
                    inspected.Add("class");
                foreach (var predicate in step.Attributes)
                    inspected.Add(predicate.Name);
            }
        }
    }
}