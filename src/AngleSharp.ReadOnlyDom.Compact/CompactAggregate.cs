using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AngleSharp.Common;
using AngleSharp.Html.Construction;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.ReadOnlyDom.Compact.Arena;
using AngleSharp.Text;
using ConstructionArena = AngleSharp.ReadOnlyDom.Compact.Arena.Arena;

namespace AngleSharp.ReadOnlyDom.Compact;

public enum CompactAggregateCardinality
{
    First,
    All,
}

public enum CompactAggregateProjectionKind
{
    Attribute,
    NormalizedText,
    Markdown,
}

/// <summary>
/// A path of one or more steps. The last step describes the matched element itself; earlier steps
/// constrain its ancestors, each related to the following step by a <see cref="CompactPathAxis"/>.
/// Predicate builders (<see cref="WithId"/>, <see cref="WithClass"/>, <see cref="WithAttribute"/>)
/// always refine the last step.
/// </summary>
public sealed class CompactAggregateSelector
{
    private readonly CompactAggregateSelectorStep[] _steps;

    private CompactAggregateSelector(CompactAggregateSelectorStep[] steps) => _steps = steps;

    internal ReadOnlySpan<CompactAggregateSelectorStep> Steps => _steps;

    private CompactAggregateSelectorStep Last => _steps[^1];

    public string TagName => Last.TagName;
    public string? Id => Last.Id;
    public string? ClassToken => Last.ClassToken;

    internal ReadOnlySpan<CompactAggregateAttributePredicate> Attributes => Last.Attributes;

    public static CompactAggregateSelector Tag(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        return new CompactAggregateSelector([new CompactAggregateSelectorStep(CompactPathAxis.Descendant, tag)]);
    }

    /// <summary>Adds a step matching at any depth below the current one.</summary>
    public CompactAggregateSelector Descendant(string tag) => Append(CompactPathAxis.Descendant, tag);

    /// <summary>Adds a step matching only as a direct child of the current one.</summary>
    public CompactAggregateSelector Child(string tag) => Append(CompactPathAxis.Child, tag);

    public CompactAggregateSelector WithId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return ReplaceLast(Last with { Id = id });
    }

    public CompactAggregateSelector WithClass(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return ReplaceLast(Last with { ClassToken = token });
    }

    public CompactAggregateSelector WithAttribute(string name, string? value = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var attributes = new CompactAggregateAttributePredicate[Last.Attributes.Length + 1];
        Last.Attributes.CopyTo(attributes, 0);
        attributes[^1] = new CompactAggregateAttributePredicate(name, value);
        return ReplaceLast(Last with { Attributes = attributes });
    }

    private CompactAggregateSelector Append(CompactPathAxis axis, string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var steps = new CompactAggregateSelectorStep[_steps.Length + 1];
        _steps.CopyTo(steps, 0);
        steps[^1] = new CompactAggregateSelectorStep(axis, tag);
        return new CompactAggregateSelector(steps);
    }

    private CompactAggregateSelector ReplaceLast(CompactAggregateSelectorStep step)
    {
        var steps = (CompactAggregateSelectorStep[])_steps.Clone();
        steps[^1] = step;
        return new CompactAggregateSelector(steps);
    }
}

internal readonly record struct CompactAggregateSelectorStep(
    CompactPathAxis Axis,
    string TagName,
    string? Id = null,
    string? ClassToken = null,
    CompactAggregateAttributePredicate[]? AttributePredicates = null
)
{
    public CompactAggregateAttributePredicate[] Attributes { get; init; } = AttributePredicates ?? [];
}

public sealed class CompactAggregateProjection
{
    private readonly CompactAggregateSelector[] _exclusions;

    private CompactAggregateProjection(
        CompactAggregateProjectionKind kind,
        CompactAggregateSelector? selector,
        string? attribute,
        CompactAggregateSelector[] exclusions
    )
    {
        Kind = kind;
        Selector = selector;
        Attribute = attribute;
        _exclusions = exclusions;
    }

    public CompactAggregateProjectionKind Kind { get; }
    public CompactAggregateSelector? Selector { get; }
    public string? Attribute { get; }
    internal ReadOnlySpan<CompactAggregateSelector> Exclusions => _exclusions;

    public static CompactAggregateProjection SelfNormalizedText() =>
        new(CompactAggregateProjectionKind.NormalizedText, null, null, []);

    public static CompactAggregateProjection FirstNormalizedText(CompactAggregateSelector selector) =>
        new(CompactAggregateProjectionKind.NormalizedText, selector, null, []);

    public static CompactAggregateProjection SelfAttribute(string attribute) => AttributeProjection(null, attribute);

    public static CompactAggregateProjection FirstAttribute(CompactAggregateSelector selector, string attribute) =>
        AttributeProjection(selector, attribute);

    public static CompactAggregateProjection SelfMarkdown(params CompactAggregateSelector[] exclusions) =>
        MarkdownProjection(null, exclusions);

    public static CompactAggregateProjection FirstMarkdown(
        CompactAggregateSelector selector,
        params CompactAggregateSelector[] exclusions
    ) => MarkdownProjection(selector, exclusions);

    private static CompactAggregateProjection AttributeProjection(CompactAggregateSelector? selector, string attribute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        return new CompactAggregateProjection(CompactAggregateProjectionKind.Attribute, selector, attribute, []);
    }

    private static CompactAggregateProjection MarkdownProjection(
        CompactAggregateSelector? selector,
        CompactAggregateSelector[] exclusions
    )
    {
        ArgumentNullException.ThrowIfNull(exclusions);
        if (exclusions.Any(static exclusion => exclusion is null))
            throw new ArgumentException("Markdown exclusions cannot contain null.", nameof(exclusions));
        return new CompactAggregateProjection(CompactAggregateProjectionKind.Markdown, selector, null, [.. exclusions]);
    }
}

public readonly record struct CompactAggregateRequirements(
    IReadOnlyList<string> InspectedAttributes,
    IReadOnlyList<string> RetainedAttributes,
    bool RetainsText,
    bool ProducesMarkdown
);

public readonly record struct CompactAggregateCounters(
    int TokensProcessed,
    int NodesMaterialized,
    int CandidateNodes,
    int MatchedScopes,
    int AttributesInspected,
    int AttributesRetained,
    int TextValuesRetained,
    int ValuesDecoded,
    int RowsProduced,
    int RowsRejected,
    int InputBytesConsumed
);

public readonly record struct CompactAggregateField(string Name, CompactExtractionValue Value);

public sealed class CompactAggregateRow
{
    private readonly CompactAggregateField[] _fields;

    internal CompactAggregateRow(CompactAggregateField[] fields) => _fields = fields;

    public IReadOnlyList<CompactAggregateField> Fields => _fields;

    public CompactExtractionValue this[string name]
    {
        get
        {
            foreach (var field in _fields)
                if (string.Equals(field.Name, name, StringComparison.Ordinal))
                    return field.Value;
            return default;
        }
    }
}

public sealed class CompactAggregateResult
{
    private readonly CompactAggregateCardinality _cardinality;

    internal CompactAggregateResult(
        CompactAggregateCardinality cardinality,
        CompactAggregateRow[] rows,
        CompactAggregateCounters counters
    )
    {
        _cardinality = cardinality;
        Rows = rows;
        Counters = counters;
    }

    public IReadOnlyList<CompactAggregateRow> Rows { get; }
    public CompactAggregateCounters Counters { get; }

    public void WriteJson(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (_cardinality == CompactAggregateCardinality.All)
        {
            writer.WriteStartArray();
            foreach (var row in Rows)
                WriteRow(writer, row);
            writer.WriteEndArray();
        }
        else if (Rows.Count == 0)
        {
            writer.WriteNullValue();
        }
        else
        {
            WriteRow(writer, Rows[0]);
        }
    }

    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteJson(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteRow(Utf8JsonWriter writer, CompactAggregateRow row)
    {
        writer.WriteStartObject();
        foreach (var field in row.Fields)
        {
            writer.WritePropertyName(field.Name);
            if (field.Value.Exists)
                writer.WriteStringValue(field.Value.Span);
            else
                writer.WriteNullValue();
        }
        writer.WriteEndObject();
    }
}

public static class CompactAggregate
{
    public static CompactAggregatePlanBuilder First(CompactAggregateSelector selector) =>
        new(selector, CompactAggregateCardinality.First);

    public static CompactAggregatePlanBuilder ForEach(CompactAggregateSelector selector) =>
        new(selector, CompactAggregateCardinality.All);
}

public sealed class CompactAggregatePlanBuilder
{
    private readonly CompactAggregateSelector _scope;
    private readonly CompactAggregateCardinality _cardinality;
    private readonly List<CompactAggregateFieldDefinition> _fields = [];

    internal CompactAggregatePlanBuilder(CompactAggregateSelector selector, CompactAggregateCardinality cardinality)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _scope = selector;
        _cardinality = cardinality;
    }

    public CompactAggregatePlanBuilder Field(string name, CompactAggregateProjection projection, bool required = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(projection);
        if (_fields.Any(field => string.Equals(field.Name, name, StringComparison.Ordinal)))
            throw new ArgumentException($"Field '{name}' already exists.", nameof(name));
        _fields.Add(new CompactAggregateFieldDefinition(name, projection, required));
        return this;
    }

    public CompactAggregatePlan Compile()
    {
        if (_fields.Count == 0)
            throw new InvalidOperationException("At least one aggregate field is required.");
        return new CompactAggregatePlan(_scope, _cardinality, [.. _fields]);
    }
}

public sealed class CompactAggregatePlan
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
        + $"markdown={Requirements.ProducesMarkdown}; termination=end-of-document; output=owned";

    internal CompactAggregateResult Evaluate(
        ConstructionArena arena,
        int root,
        CompactAggregateExecutionState state,
        int inputBytesConsumed
    )
    {
        var rows = new List<CompactAggregateRow>();
        Visit(root);
        return new CompactAggregateResult(Cardinality, [.. rows], state.Snapshot(inputBytesConsumed));

        bool Visit(int handle)
        {
            if (arena.Kind(handle) == CompactNodeKind.Element)
            {
                state.CandidateNode();
                if (Matches(arena, handle, _scope, state))
                {
                    state.MatchedScope();
                    if (TryProject(arena, handle, state, out var row))
                    {
                        rows.Add(row);
                        state.RowProduced();
                        if (Cardinality == CompactAggregateCardinality.First)
                            return true;
                    }
                    else
                    {
                        state.RowRejected();
                    }
                }
            }

            for (var child = arena.FirstChild(handle); child >= 0; child = arena.NextSibling(child))
                if (Visit(child))
                    return true;
            return false;
        }
    }

    private bool TryProject(
        ConstructionArena arena,
        int scope,
        CompactAggregateExecutionState state,
        out CompactAggregateRow row
    )
    {
        var values = new CompactAggregateField[_fields.Length];
        for (var index = 0; index < _fields.Length; index++)
        {
            var field = _fields[index];
            var projection = field.Projection;
            var target = projection.Selector is null ? scope : FindFirst(arena, scope, projection.Selector, state);
            CompactExtractionValue value = default;
            if (target >= 0)
            {
                value = projection.Kind switch
                {
                    CompactAggregateProjectionKind.Attribute => ProjectAttribute(
                        arena,
                        target,
                        projection.Attribute!,
                        state
                    ),
                    CompactAggregateProjectionKind.NormalizedText => new CompactExtractionValue(
                        NormalizeText(arena, target)
                    ),
                    CompactAggregateProjectionKind.Markdown => new CompactExtractionValue(
                        ProjectMarkdown(arena, target, projection.Exclusions, state)
                    ),
                    _ => throw new InvalidOperationException("Unknown aggregate projection."),
                };
            }
            if (field.Required && !value.Exists)
            {
                row = null!;
                return false;
            }
            values[index] = new CompactAggregateField(field.Name, value);
        }
        row = new CompactAggregateRow(values);
        return true;
    }

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
            if (projection.Kind == CompactAggregateProjectionKind.Markdown)
            {
                retained.Add("href");
                foreach (var exclusion in projection.Exclusions)
                    AddSelector(exclusion);
            }
        }
        retained.UnionWith(inspected);
        return new CompactAggregateRequirements(
            inspected.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            retained.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            fields.Any(static field => field.Projection.Kind != CompactAggregateProjectionKind.Attribute),
            fields.Any(static field => field.Projection.Kind == CompactAggregateProjectionKind.Markdown)
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

    private static int FindFirst(
        ConstructionArena arena,
        int scope,
        CompactAggregateSelector selector,
        CompactAggregateExecutionState state
    )
    {
        for (var child = arena.FirstChild(scope); child >= 0; child = arena.NextSibling(child))
        {
            if (arena.Kind(child) == CompactNodeKind.Element)
            {
                state.CandidateNode();
                if (Matches(arena, child, selector, state))
                    return child;
            }
            var nested = FindFirst(arena, child, selector, state);
            if (nested >= 0)
                return nested;
        }
        return -1;
    }

    private static bool Matches(
        ConstructionArena arena,
        int handle,
        CompactAggregateSelector selector,
        CompactAggregateExecutionState state
    ) => MatchesChain(arena, handle, selector.Steps, selector.Steps.Length - 1, state);

    /// <summary>
    /// Matches the step chain right to left: the candidate must satisfy the last step, then each
    /// earlier step must be satisfied by an ancestor. Descendant steps try every ancestor rather than
    /// the nearest match, so a chain like <c>div &gt;&gt; section &gt;&gt; p</c> still matches when an
    /// intermediate ancestor also carries the tag of an earlier step.
    /// </summary>
    private static bool MatchesChain(
        ConstructionArena arena,
        int handle,
        ReadOnlySpan<CompactAggregateSelectorStep> steps,
        int index,
        CompactAggregateExecutionState state
    )
    {
        if (!MatchesStep(arena, handle, steps[index], state))
            return false;
        if (index == 0)
            return true;

        var parent = arena.Parent(handle);
        if (steps[index].Axis == CompactPathAxis.Child)
            return parent >= 0 && MatchesChain(arena, parent, steps, index - 1, state);

        for (var ancestor = parent; ancestor >= 0; ancestor = arena.Parent(ancestor))
            if (MatchesChain(arena, ancestor, steps, index - 1, state))
                return true;
        return false;
    }

    private static bool MatchesStep(
        ConstructionArena arena,
        int handle,
        in CompactAggregateSelectorStep step,
        CompactAggregateExecutionState state
    )
    {
        if (arena.Kind(handle) != CompactNodeKind.Element)
            return false;
        if (!arena.LocalName(handle).Memory.Span.Equals(step.TagName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (step.Id is not null && !MatchAttribute(arena, handle, "id", step.Id, false, state))
            return false;
        if (step.ClassToken is not null && !MatchAttribute(arena, handle, "class", step.ClassToken, true, state))
            return false;
        foreach (var predicate in step.Attributes)
            if (!MatchAttribute(arena, handle, predicate.Name, predicate.Value, false, state))
                return false;
        return true;
    }

    private static bool MatchAttribute(
        ConstructionArena arena,
        int handle,
        string name,
        string? value,
        bool token,
        CompactAggregateExecutionState state
    )
    {
        for (
            var attribute = arena.FirstAttributeHandle(handle);
            attribute >= 0;
            attribute = arena.NextAttribute(attribute)
        )
        {
            state.AttributeInspected();
            if (!arena.AttributeName(attribute).Memory.Span.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (value is null)
                return true;
            var actual = arena.AttributeValue(attribute).Memory.Span;
            return token ? ContainsToken(actual, value) : actual.SequenceEqual(value);
        }
        return false;
    }

    private static CompactExtractionValue ProjectAttribute(
        ConstructionArena arena,
        int handle,
        string name,
        CompactAggregateExecutionState state
    )
    {
        for (
            var attribute = arena.FirstAttributeHandle(handle);
            attribute >= 0;
            attribute = arena.NextAttribute(attribute)
        )
        {
            state.AttributeInspected();
            if (arena.AttributeName(attribute).Memory.Span.Equals(name, StringComparison.OrdinalIgnoreCase))
                return new CompactExtractionValue(arena.AttributeValue(attribute).Memory.ToString());
        }
        return default;
    }

    private static bool ContainsToken(ReadOnlySpan<char> values, ReadOnlySpan<char> wanted)
    {
        while (!values.IsEmpty)
        {
            values = values.TrimStart();
            var end = 0;
            while (end < values.Length && !char.IsWhiteSpace(values[end]))
                end++;
            if (end == values.Length)
                end = -1;
            var token = end < 0 ? values : values[..end];
            if (token.SequenceEqual(wanted))
                return true;
            if (end < 0)
                return false;
            values = values[(end + 1)..];
        }
        return false;
    }

    private static string NormalizeText(ConstructionArena arena, int target)
    {
        var output = new StringBuilder();
        var pendingSpace = false;
        Append(target);
        return output.ToString();

        void Append(int handle)
        {
            if (arena.Kind(handle) == CompactNodeKind.Text)
            {
                foreach (var character in arena.Value(handle).Memory.Span)
                {
                    if (char.IsWhiteSpace(character))
                    {
                        pendingSpace = output.Length != 0;
                        continue;
                    }
                    if (pendingSpace)
                    {
                        output.Append(' ');
                        pendingSpace = false;
                    }
                    output.Append(character);
                }
            }
            for (var child = arena.FirstChild(handle); child >= 0; child = arena.NextSibling(child))
                Append(child);
        }
    }

    private static string ProjectMarkdown(
        ConstructionArena arena,
        int target,
        ReadOnlySpan<CompactAggregateSelector> exclusions,
        CompactAggregateExecutionState state
    ) => new CompactMarkdownWriter(arena, exclusions.ToArray(), state).Write(target);
}

internal readonly record struct CompactAggregateAttributePredicate(string Name, string? Value);

internal readonly record struct CompactAggregateFieldDefinition(
    string Name,
    CompactAggregateProjection Projection,
    bool Required
);

internal sealed class CompactAggregateDefinition(CompactAggregatePlan plan) : ICompactConstructionViewDefinition
{
    public ICompactConstructionViewState CreateState(TextSource source) =>
        new CompactAggregateExecutionState(SourceIdentity(source), plan);

    private static string? SourceIdentity(TextSource source) =>
        source.GetUnderlyingTextSource() is StringTextSource text ? text.Text : null;
}

internal sealed class CompactAggregateExecutionState : ICompactConstructionViewState
{
    private readonly string? _source;
    private readonly CompactAggregatePlan _plan;
    private int _tokensProcessed;
    private int _nodesMaterialized;
    private int _candidateNodes;
    private int _matchedScopes;
    private int _attributesInspected;
    private int _attributesRetained;
    private int _textValuesRetained;
    private int _valuesDecoded;
    private int _rowsProduced;
    private int _rowsRejected;

    public CompactAggregateExecutionState(string? source, CompactAggregatePlan plan)
    {
        _source = source;
        _plan = plan;
    }

    public void SetTokensProcessed(int count) => _tokensProcessed = count;

    public void NodeMaterialized() => _nodesMaterialized++;

    public void CandidateNode() => _candidateNodes++;

    public void MatchedScope() => _matchedScopes++;

    public void AttributeInspected() => _attributesInspected++;

    public void RowProduced() => _rowsProduced++;

    public void RowRejected() => _rowsRejected++;

    public void AttributeRetained(StringOrMemory value)
    {
        _attributesRetained++;
        ObserveDecoded(value);
    }

    public void CompleteAttributes(ConstructionArena arena, int handle) { }

    public StringOrMemory SelectTextValue(StringOrMemory value)
    {
        _textValuesRetained++;
        ObserveDecoded(value);
        return value;
    }

    public CompactAggregateResult CreateResult(ConstructionArena arena, int root, int inputBytesConsumed) =>
        _plan.Evaluate(arena, root, this, inputBytesConsumed);

    public CompactAggregateCounters Snapshot(int inputBytesConsumed) =>
        new(
            _tokensProcessed,
            _nodesMaterialized,
            _candidateNodes,
            _matchedScopes,
            _attributesInspected,
            _attributesRetained,
            _textValuesRetained,
            _valuesDecoded,
            _rowsProduced,
            _rowsRejected,
            inputBytesConsumed
        );

    private void ObserveDecoded(StringOrMemory value)
    {
        if (
            !MemoryMarshal.TryGetString(value.Memory, out var backing, out _, out _)
            || !ReferenceEquals(backing, _source)
        )
            _valuesDecoded++;
    }
}

internal sealed class CompactMarkdownWriter
{
    private readonly ConstructionArena _arena;
    private readonly CompactAggregateSelector[] _exclusions;
    private readonly CompactAggregateExecutionState _state;
    private readonly StringBuilder _output = new();
    private bool _pendingSpace;

    public CompactMarkdownWriter(
        ConstructionArena arena,
        CompactAggregateSelector[] exclusions,
        CompactAggregateExecutionState state
    )
    {
        _arena = arena;
        _exclusions = exclusions;
        _state = state;
    }

    public string Write(int target)
    {
        VisitChildren(target, inPre: false);
        return _output.ToString().Trim();
    }

    private void Visit(int handle, bool inPre)
    {
        if (_arena.Kind(handle) == CompactNodeKind.Text)
        {
            if (inPre)
                _output.Append(_arena.Value(handle).Memory.Span);
            else
                AppendNormalized(_arena.Value(handle).Memory.Span);
            return;
        }
        if (_arena.Kind(handle) != CompactNodeKind.Element || IsExcluded(handle))
            return;

        var tag = _arena.LocalName(handle).Memory.Span;
        if (
            tag.Equals("script", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("style", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("template", StringComparison.OrdinalIgnoreCase)
        )
            return;

        if (TryHeadingLevel(tag, out var level))
        {
            EnsureNewlines(2);
            AppendLiteral(new string('#', level));
            AppendLiteral(" ");
            VisitChildren(handle, false);
            EnsureNewlines(2);
        }
        else if (tag.Equals("p", StringComparison.OrdinalIgnoreCase))
        {
            EnsureNewlines(2);
            VisitChildren(handle, false);
            EnsureNewlines(2);
        }
        else if (tag.Equals("br", StringComparison.OrdinalIgnoreCase))
        {
            EnsureNewlines(1);
        }
        else if (
            tag.Equals("ul", StringComparison.OrdinalIgnoreCase) || tag.Equals("ol", StringComparison.OrdinalIgnoreCase)
        )
        {
            EnsureNewlines(2);
            VisitChildren(handle, false);
            EnsureNewlines(2);
        }
        else if (tag.Equals("li", StringComparison.OrdinalIgnoreCase))
        {
            EnsureNewlines(1);
            AppendLiteral("- ");
            VisitChildren(handle, false);
            EnsureNewlines(1);
        }
        else if (
            tag.Equals("strong", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("b", StringComparison.OrdinalIgnoreCase)
        )
        {
            FlushPendingSpace();
            AppendLiteral("**");
            VisitChildren(handle, false);
            AppendLiteral("**");
        }
        else if (
            tag.Equals("em", StringComparison.OrdinalIgnoreCase) || tag.Equals("i", StringComparison.OrdinalIgnoreCase)
        )
        {
            FlushPendingSpace();
            AppendLiteral("*");
            VisitChildren(handle, false);
            AppendLiteral("*");
        }
        else if (tag.Equals("a", StringComparison.OrdinalIgnoreCase))
        {
            FlushPendingSpace();
            AppendLiteral("[");
            VisitChildren(handle, false);
            AppendLiteral("]");
            if (TryFindAttribute(handle, "href", out var href))
            {
                AppendLiteral("(");
                AppendLiteral(href.ToString());
                AppendLiteral(")");
            }
        }
        else if (tag.Equals("pre", StringComparison.OrdinalIgnoreCase))
        {
            EnsureNewlines(2);
            AppendLiteral("```text\n");
            VisitChildren(handle, true);
            EnsureNewlines(1);
            AppendLiteral("```");
            EnsureNewlines(2);
        }
        else if (tag.Equals("code", StringComparison.OrdinalIgnoreCase) && !inPre)
        {
            FlushPendingSpace();
            AppendLiteral("`");
            VisitChildren(handle, true);
            AppendLiteral("`");
        }
        else
        {
            VisitChildren(handle, inPre);
        }
    }

    private void VisitChildren(int handle, bool inPre)
    {
        for (var child = _arena.FirstChild(handle); child >= 0; child = _arena.NextSibling(child))
            Visit(child, inPre);
    }

    private bool IsExcluded(int handle)
    {
        foreach (var exclusion in _exclusions)
            if (MatchesExclusion(handle, exclusion))
                return true;
        return false;
    }

    private bool MatchesExclusion(int handle, CompactAggregateSelector selector) =>
        MatchesExclusionChain(handle, selector.Steps, selector.Steps.Length - 1);

    private bool MatchesExclusionChain(int handle, ReadOnlySpan<CompactAggregateSelectorStep> steps, int index)
    {
        if (!MatchesExclusionStep(handle, steps[index]))
            return false;
        if (index == 0)
            return true;

        var parent = _arena.Parent(handle);
        if (steps[index].Axis == CompactPathAxis.Child)
            return parent >= 0 && MatchesExclusionChain(parent, steps, index - 1);

        for (var ancestor = parent; ancestor >= 0; ancestor = _arena.Parent(ancestor))
            if (MatchesExclusionChain(ancestor, steps, index - 1))
                return true;
        return false;
    }

    private bool MatchesExclusionStep(int handle, in CompactAggregateSelectorStep step)
    {
        if (_arena.Kind(handle) != CompactNodeKind.Element)
            return false;
        if (!_arena.LocalName(handle).Memory.Span.Equals(step.TagName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (step.Id is not null && !MatchAttribute("id", step.Id, false))
            return false;
        if (step.ClassToken is not null && !MatchAttribute("class", step.ClassToken, true))
            return false;
        foreach (var predicate in step.Attributes)
            if (!MatchAttribute(predicate.Name, predicate.Value, false))
                return false;
        return true;

        bool MatchAttribute(string name, string? value, bool token)
        {
            if (!TryFindAttribute(handle, name, out var actual))
                return false;
            return value is null || (token ? ContainsToken(actual, value) : actual.SequenceEqual(value));
        }
    }

    private bool TryFindAttribute(int handle, string name, out ReadOnlySpan<char> value)
    {
        for (
            var attribute = _arena.FirstAttributeHandle(handle);
            attribute >= 0;
            attribute = _arena.NextAttribute(attribute)
        )
        {
            _state.AttributeInspected();
            if (_arena.AttributeName(attribute).Memory.Span.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = _arena.AttributeValue(attribute).Memory.Span;
                return true;
            }
        }
        value = default;
        return false;
    }

    private void AppendNormalized(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                _pendingSpace = _output.Length != 0;
                continue;
            }
            if (_pendingSpace && _output.Length != 0 && !char.IsWhiteSpace(_output[^1]))
                _output.Append(' ');
            _pendingSpace = false;
            _output.Append(character);
        }
    }

    private void AppendLiteral(string text)
    {
        _pendingSpace = false;
        _output.Append(text);
    }

    private void FlushPendingSpace()
    {
        if (_pendingSpace && _output.Length != 0 && !char.IsWhiteSpace(_output[^1]))
            _output.Append(' ');
        _pendingSpace = false;
    }

    private void EnsureNewlines(int count)
    {
        _pendingSpace = false;
        var present = 0;
        for (var index = _output.Length - 1; index >= 0 && _output[index] == '\n'; index--)
            present++;
        while (present++ < count)
            _output.Append('\n');
    }

    private static bool TryHeadingLevel(ReadOnlySpan<char> tag, out int level)
    {
        if (tag.Length == 2 && (tag[0] == 'h' || tag[0] == 'H') && tag[1] is >= '1' and <= '6')
        {
            level = tag[1] - '0';
            return true;
        }
        level = 0;
        return false;
    }

    private static bool ContainsToken(ReadOnlySpan<char> values, ReadOnlySpan<char> wanted)
    {
        while (!values.IsEmpty)
        {
            values = values.TrimStart();
            var end = 0;
            while (end < values.Length && !char.IsWhiteSpace(values[end]))
                end++;
            if (values[..end].SequenceEqual(wanted))
                return true;
            if (end == values.Length)
                return false;
            values = values[(end + 1)..];
        }
        return false;
    }
}
