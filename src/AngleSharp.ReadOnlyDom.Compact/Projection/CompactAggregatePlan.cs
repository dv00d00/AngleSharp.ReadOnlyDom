using System.Text;
using AngleSharp.Html.Construction;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.ReadOnlyDom.Compact.Arena;
using AngleSharp.Text;
using ConstructionArena = AngleSharp.ReadOnlyDom.Compact.Arena.Arena;

namespace AngleSharp.ReadOnlyDom.Compact;

public sealed partial class CompactAggregatePlan
{
    private readonly CompactAggregateSelector _scope;
    private readonly CompactAggregateFieldDefinition[] _fields;
    private readonly ArenaConstructionFactory _factory;
    private readonly string[] _retainedAttributes;
    private readonly IBrowsingContext _context;
    private readonly HtmlParserOptions _parserOptions;

    internal CompactAggregatePlan(
        CompactAggregateSelector scope,
        CompactAggregateCardinality cardinality,
        CompactAggregateFieldDefinition[] fields
    )
    {
        _scope = scope;
        Cardinality = cardinality;
        _fields = fields;
        Requirements = BuildRequirements(scope, fields);
        _retainedAttributes = [.. Requirements.RetainedAttributes];

        _factory = new ArenaConstructionFactory(
            new CompactParserHints(),
            trackSourceReferences: false,
            CompactMetadataOptions.None,
            CompactDocumentLayout.FrozenColumns,
            new CompactAggregateDefinition(this)
        );
        _context = BrowsingContext.New(Configuration.Default.With(_ => _factory));
        _parserOptions = CompactParser.CreateParserOptions(CompactMetadataOptions.None);
        _parserOptions.ShouldEmitAttribute = ShouldRetainAttribute;
    }

    public CompactAggregateCardinality Cardinality { get; }
    public CompactAggregateRequirements Requirements { get; }

    public CompactAggregateResult Execute(string source)
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
            (IHtmlTreeConstructionFactory<ArenaDocument, ArenaHandle>)_factory,
            middleware
        );
        try
        {
            document.SetTokensProcessed(tokensProcessed);
            var consumed = Math.Min(source.Length, document.Source.Index);
            return document.CreateAggregateResult(Encoding.UTF8.GetByteCount(source.AsSpan(0, consumed)));
        }
        finally
        {
            document.Dispose();
        }
    }

    public string Explain() =>
        $"mode=eof-construction-aggregate; cardinality={Cardinality.ToString().ToLowerInvariant()}; "
        + $"fields={_fields.Length}; inspect=[{string.Join(',', Requirements.InspectedAttributes)}]; "
        + $"retain=[{string.Join(',', Requirements.RetainedAttributes)}]; text={Requirements.RetainsText}; "
        + "termination=end-of-document; output=owned";

    private bool ShouldRetainAttribute(ref StructHtmlToken token, ReadOnlyMemory<char> name)
    {
        var attribute = name.Span;
        foreach (var retained in _retainedAttributes)
            if (attribute.Equals(retained, StringComparison.OrdinalIgnoreCase))
                return true;
        return CompactConstructionAttributePolicy.IsRequiredByTreeBuilder(ref token, attribute);
    }

    private static CompactAggregateRequirements BuildRequirements(
        CompactAggregateSelector scope,
        CompactAggregateFieldDefinition[] fields
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
        return new CompactAggregateRequirements(
            inspected.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            retained.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            fields.Any(static field => field.Projection.Kind != CompactAggregateProjectionKind.Attribute)
        );

        void AddSelector(CompactAggregateSelector selector)
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
