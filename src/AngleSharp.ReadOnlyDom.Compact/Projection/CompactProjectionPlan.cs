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
    private readonly Lazy<(ArenaConstructionFactory Factory, IBrowsingContext Context)> _diagnosticRuntime;
    private readonly int[] _evaluationOrder;
    private readonly HtmlParserOptions _parserOptions;
    private readonly string[] _retainedAttributes;
    private readonly CompactProjectionSelector _scope;
    private readonly int[] _targetSlots;
    private readonly int _targetSlotCount;

    internal CompactProjectionPlan(
        CompactProjectionSelector scope,
        CompactProjectionCardinality cardinality,
        CompactProjectionFieldDefinition[] fields
    )
    {
        _scope = scope;
        Cardinality = cardinality;
        _fields = fields;
        _evaluationOrder = Enumerable
            .Range(0, fields.Length)
            .OrderByDescending(index => fields[index].Required)
            .ToArray();
        (_targetSlots, _targetSlotCount) = BuildTargetSlots(fields);
        Requirements = BuildRequirements(scope, fields);
        _retainedAttributes = [.. Requirements.RetainedAttributes];

        _factory = CreateFactory(collectDiagnostics: false);
        _diagnosticRuntime = new Lazy<(ArenaConstructionFactory, IBrowsingContext)>(
            () =>
            {
                var factory = CreateFactory(collectDiagnostics: true);
                return (factory, BrowsingContext.New(Configuration.Default.With(_ => factory)));
            }
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
        return Execute(source, _factory, _context, collectDiagnostics: false);
    }

    internal CompactProjectionResult ExecuteWithDiagnostics(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var runtime = _diagnosticRuntime.Value;
        return Execute(source, runtime.Factory, runtime.Context, collectDiagnostics: true);
    }

    private ArenaConstructionFactory CreateFactory(bool collectDiagnostics)
    {
        return new ArenaConstructionFactory(
            new CompactParserHints(),
            false,
            CompactMetadataOptions.None,
            CompactDocumentLayout.FrozenColumns,
            new CompactProjectionDefinition(this, collectDiagnostics)
        );
    }

    private CompactProjectionResult Execute(
        string source,
        ArenaConstructionFactory factory,
        IBrowsingContext context,
        bool collectDiagnostics
    )
    {
        var parser = new HtmlParser(_parserOptions, context);
        var textSource = new TextSource(new StringTextSource(source));
        var tokensProcessed = 0;
        TokenizerMiddleware middleware = (ref StructHtmlToken token, TokenConsumer next) =>
        {
            tokensProcessed++;
            next(ref token);
            return TokenConsumptionResult.Continue;
        };
        var document = collectDiagnostics
            ? parser.ParseDocument(textSource, factory, middleware)
            : parser.ParseDocument(textSource, factory);
        try
        {
            if (!collectDiagnostics)
                return document.CreateProjectionResult(0);

            document.SetTokensProcessed(tokensProcessed);
            var consumed = Math.Min(source.Length, document.Source.Index);
            var inputBytesConsumed = Encoding.UTF8.GetByteCount(source.AsSpan(0, consumed));
            return document.CreateProjectionResult(inputBytesConsumed);
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

    private static (int[] Slots, int Count) BuildTargetSlots(CompactProjectionFieldDefinition[] fields)
    {
        var slots = new int[fields.Length];
        var selectors = new List<CompactProjectionSelector>();
        for (var index = 0; index < fields.Length; index++)
        {
            var selector = fields[index].Projection.Selector;
            if (selector is null)
            {
                slots[index] = -1;
                continue;
            }

            var slot = -1;
            for (var candidate = 0; candidate < selectors.Count; candidate++)
                if (selector.HasSameStepsAs(selectors[candidate]))
                {
                    slot = candidate;
                    break;
                }

            if (slot < 0)
            {
                slot = selectors.Count;
                selectors.Add(selector);
            }

            slots[index] = slot;
        }

        return (slots, selectors.Count);
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
