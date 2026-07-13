using System.Text;

namespace AngleSharp.ReadOnlyDom.Compact;

public enum CompactPathAxis
{
    Descendant,
    Child,
}

public enum CompactPlanCardinality
{
    First,
    All,
}

public enum CompactValueOwnership
{
    None,
    BorrowedDocumentSlice,
    Owned,
}

public readonly record struct CompactPlanRequirements(
    IReadOnlyList<string> InspectedAttributes,
    IReadOnlyList<string> RetainedAttributes,
    IReadOnlyList<string> MaterializedAttributes,
    bool RetainsText,
    CompactMetadataOptions MetadataOptions
);

public readonly record struct CompactExecutionCounters(
    int CandidateNodes,
    int AttributesInspected,
    int MatchedNodes,
    int RowsProduced,
    int RowsRejected,
    int BorrowedValues,
    int OwnedValues
);

public readonly struct CompactExtractionValue
{
    private readonly CompactDocument? _document;
    private readonly int _start;
    private readonly int _length;
    private readonly string? _owned;

    internal CompactExtractionValue(CompactDocument document, int start, int length)
    {
        _document = document;
        _start = start;
        _length = length;
        _owned = null;
        Exists = true;
        Ownership = CompactValueOwnership.BorrowedDocumentSlice;
    }

    internal CompactExtractionValue(string owned)
    {
        _document = null;
        _start = 0;
        _length = owned.Length;
        _owned = owned;
        Exists = true;
        Ownership = CompactValueOwnership.Owned;
    }

    public bool Exists { get; }
    public CompactValueOwnership Ownership { get; }
    public ReadOnlySpan<char> Span =>
        !Exists ? default : _owned is not null ? _owned.AsSpan() : _document!.GetValue(_start, _length);

    public string Own() => Exists ? (_owned ?? Span.ToString()) : string.Empty;
    public override string ToString() => Own();
}

public readonly record struct CompactExtractionField(string Name, CompactExtractionValue Value);

public sealed class CompactExtractionRow
{
    private readonly CompactExtractionField[] _fields;

    internal CompactExtractionRow(int handle, CompactExtractionField[] fields)
    {
        Handle = handle;
        _fields = fields;
    }

    public int Handle { get; }
    public IReadOnlyList<CompactExtractionField> Fields => _fields;

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

public sealed class CompactExtractionResult
{
    internal CompactExtractionResult(CompactExtractionRow[] rows, CompactExecutionCounters counters)
    {
        Rows = rows;
        Counters = counters;
    }

    public IReadOnlyList<CompactExtractionRow> Rows { get; }
    public CompactExecutionCounters Counters { get; }
}

public sealed class CompactBoundExtractionPlan
{
    private readonly CompactExtractionPlan _plan;
    private readonly CompactDocument _document;
    private readonly ResolvedStep[] _steps;
    private readonly ResolvedProjection[] _projections;

    internal CompactBoundExtractionPlan(
        CompactExtractionPlan plan,
        CompactDocument document,
        ResolvedStep[] steps,
        ResolvedProjection[] projections
    )
    {
        _plan = plan;
        _document = document;
        _steps = steps;
        _projections = projections;
    }

    public CompactExtractionResult Execute() => _plan.Execute(_document, _steps, _projections);
}

public sealed class CompactExtractionPlanBuilder
{
    private readonly List<StepBuilder> _steps;
    private readonly List<ProjectionBuilder> _projections = [];
    private CompactPlanCardinality _cardinality = CompactPlanCardinality.All;

    internal CompactExtractionPlanBuilder(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        _steps = [new StepBuilder(CompactPathAxis.Descendant, tag)];
    }

    public CompactExtractionPlanBuilder Descendant(string tag) => AddStep(CompactPathAxis.Descendant, tag);
    public CompactExtractionPlanBuilder Child(string tag) => AddStep(CompactPathAxis.Child, tag);

    public CompactExtractionPlanBuilder WithId(string id) => WithAttribute("id", id);

    public CompactExtractionPlanBuilder WithClass(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        Current.ClassToken = token;
        return this;
    }

    public CompactExtractionPlanBuilder WithAttribute(string name, string? value = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Current.Attributes.Add(new AttributePredicateBuilder(name, value));
        return this;
    }

    public CompactExtractionPlanBuilder TakeFirst()
    {
        _cardinality = CompactPlanCardinality.First;
        return this;
    }

    public CompactExtractionPlanBuilder TakeAll()
    {
        _cardinality = CompactPlanCardinality.All;
        return this;
    }

    public CompactExtractionPlanBuilder SelectAttribute(
        string field,
        string attribute,
        bool required = false,
        bool own = false
    )
    {
        AddProjection(field, attribute, required, own, normalizedText: false);
        return this;
    }

    public CompactExtractionPlanBuilder SelectNormalizedText(string field, bool required = false)
    {
        AddProjection(field, null, required, own: true, normalizedText: true);
        return this;
    }

    public CompactExtractionPlan Compile()
    {
        if (_projections.Count == 0)
            throw new InvalidOperationException("At least one projection is required.");
        if (_projections.Select(static projection => projection.Field).Distinct(StringComparer.Ordinal).Count()
            != _projections.Count)
            throw new InvalidOperationException("Projection field names must be unique.");

        return new CompactExtractionPlan(
            _steps.Select(static step => step.Build()).ToArray(),
            _projections.Select(static projection => projection.Build()).ToArray(),
            _cardinality
        );
    }

    private StepBuilder Current => _steps[^1];

    private CompactExtractionPlanBuilder AddStep(CompactPathAxis axis, string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        _steps.Add(new StepBuilder(axis, tag));
        return this;
    }

    private void AddProjection(string field, string? attribute, bool required, bool own, bool normalizedText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        if (!normalizedText)
            ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        _projections.Add(new ProjectionBuilder(field, attribute, required, own, normalizedText));
    }

    private sealed class StepBuilder(CompactPathAxis axis, string tag)
    {
        public string? ClassToken { get; set; }
        public List<AttributePredicateBuilder> Attributes { get; } = [];

        public PlanStep Build() =>
            new(axis, tag, ClassToken, Attributes.Select(static predicate => predicate.Build()).ToArray());
    }

    private readonly record struct AttributePredicateBuilder(string Name, string? Value)
    {
        public AttributePredicate Build() => new(Name, Value);
    }

    private readonly record struct ProjectionBuilder(
        string Field,
        string? Attribute,
        bool Required,
        bool Own,
        bool NormalizedText
    )
    {
        public PlanProjection Build() => new(Field, Attribute, Required, Own, NormalizedText);
    }
}

public sealed class CompactExtractionPlan
{
    private readonly PlanStep[] _steps;
    private readonly PlanProjection[] _projections;

    internal CompactExtractionPlan(
        PlanStep[] steps,
        PlanProjection[] projections,
        CompactPlanCardinality cardinality
    )
    {
        _steps = steps;
        _projections = projections;
        Cardinality = cardinality;
        Requirements = BuildRequirements(steps, projections);
    }

    public CompactPlanCardinality Cardinality { get; }
    public CompactPlanRequirements Requirements { get; }

    public static CompactExtractionPlanBuilder Start(string tag) => new(tag);

    public CompactBoundExtractionPlan Bind(CompactDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var steps = _steps.Select(step => Resolve(step, document)).ToArray();
        var projections = _projections.Select(projection => Resolve(projection, document)).ToArray();
        return new CompactBoundExtractionPlan(this, document, steps, projections);
    }

    public CompactExtractionResult Execute(CompactDocument document) => Bind(document).Execute();

    internal CompactExtractionResult Execute(
        CompactDocument document,
        ResolvedStep[] steps,
        ResolvedProjection[] projections
    )
    {
        var counters = new MutableCounters();
        List<Node> matches = [];

        for (var stepIndex = 0; stepIndex < steps.Length; stepIndex++)
        {
            var step = steps[stepIndex];
            var next = new List<Node>();
            HashSet<int>? seen = stepIndex == 0 ? null : [];
            if (stepIndex == 0)
            {
                foreach (var candidate in document.Elements(step.TagId))
                    AddIfMatch(candidate, step, document, next, seen, counters);
            }
            else
            {
                foreach (var parent in matches)
                {
                    if (step.Axis == CompactPathAxis.Descendant)
                    {
                        foreach (var candidate in parent.Elements(step.TagId))
                            AddIfMatch(candidate, step, document, next, seen, counters);
                    }
                    else
                    {
                        foreach (var candidate in parent.Children())
                            if (candidate.Is(step.TagId))
                                AddIfMatch(candidate, step, document, next, seen, counters);
                    }
                }
            }
            matches = next;
            if (matches.Count == 0)
                break;
        }

        var rows = new List<CompactExtractionRow>();
        foreach (var match in matches)
        {
            if (TryProject(match, document, projections, counters, out var row))
            {
                rows.Add(row);
                counters.RowsProduced++;
                if (Cardinality == CompactPlanCardinality.First)
                    break;
            }
            else
            {
                counters.RowsRejected++;
            }
        }

        return new CompactExtractionResult(rows.ToArray(), counters.Snapshot());
    }

    public string Explain()
    {
        var text = new StringBuilder();
        text.Append("mode=interpreted compact-preorder; cardinality=")
            .Append(Cardinality.ToString().ToLowerInvariant())
            .AppendLine();
        for (var index = 0; index < _steps.Length; index++)
        {
            var step = _steps[index];
            text.Append("step ").Append(index).Append(": ").Append(step.Axis.ToString().ToLowerInvariant())
                .Append(" tag=").Append(step.Tag);
            if (step.ClassToken is not null)
                text.Append(" class~=").Append(step.ClassToken);
            foreach (var predicate in step.Attributes)
                text.Append(' ').Append(predicate.Name).Append(predicate.Value is null ? " exists" : "=").Append(predicate.Value);
            text.AppendLine();
        }
        foreach (var projection in _projections)
        {
            text.Append("project ").Append(projection.Field).Append(": ")
                .Append(projection.NormalizedText ? "normalized-subtree-text" : $"attribute({projection.Attribute})")
                .Append(projection.Required ? " required" : " optional")
                .Append(projection.Own ? " owned" : " borrowed")
                .AppendLine();
        }
        text.Append("payloads: inspect=[").AppendJoin(',', Requirements.InspectedAttributes)
            .Append("]; retain=[").AppendJoin(',', Requirements.RetainedAttributes)
            .Append("]; materialize=[").AppendJoin(',', Requirements.MaterializedAttributes)
            .Append("]; text=").Append(Requirements.RetainsText)
            .Append("; sidecars=none; termination=")
            .Append(Cardinality == CompactPlanCardinality.First ? "first valid row after path evaluation" : "end of candidate ranges")
            .Append("; state=two candidate lists + duplicate handle set");
        return text.ToString();
    }

    private static void AddIfMatch(
        Node candidate,
        ResolvedStep step,
        CompactDocument document,
        List<Node> destination,
        HashSet<int>? seen,
        MutableCounters counters
    )
    {
        counters.CandidateNodes++;
        if (seen is not null && !seen.Add(candidate.Handle))
            return;
        if (step.ClassToken is not null
            && !MatchAttribute(candidate, document, step.ClassId, step.ClassToken, token: true, counters))
            return;
        foreach (var predicate in step.Attributes)
            if (!MatchAttribute(candidate, document, predicate.NameId, predicate.Value, token: false, counters))
                return;
        destination.Add(candidate);
        counters.MatchedNodes++;
    }

    private static bool MatchAttribute(
        Node node,
        CompactDocument document,
        ushort nameId,
        string? value,
        bool token,
        MutableCounters counters
    )
    {
        var inspected = 0;
        var found = document.TryGetAttribute(node.Handle, nameId, out var attribute, ref inspected);
        counters.AttributesInspected += inspected;
        if (!found)
            return false;
        if (value is null)
            return true;
        var actual = document.GetValue(attribute.ValueStart, attribute.ValueLength);
        return token ? ContainsToken(actual, value) : actual.SequenceEqual(value);
    }

    private bool TryProject(
        Node node,
        CompactDocument document,
        ResolvedProjection[] projections,
        MutableCounters counters,
        out CompactExtractionRow row
    )
    {
        var fields = new CompactExtractionField[projections.Length];
        for (var index = 0; index < projections.Length; index++)
        {
            var projection = projections[index];
            CompactExtractionValue value;
            if (projection.NormalizedText)
            {
                var owned = Normalize(node.Text());
                value = new CompactExtractionValue(owned);
                counters.OwnedValues++;
            }
            else
            {
                var inspected = 0;
                var found = document.TryGetAttribute(
                    node.Handle,
                    projection.AttributeId,
                    out var attribute,
                    ref inspected
                );
                counters.AttributesInspected += inspected;
                if (!found)
                {
                    if (projection.Required)
                    {
                        row = null!;
                        return false;
                    }
                    value = default;
                }
                else if (projection.Own)
                {
                    value = new CompactExtractionValue(
                        document.GetValue(attribute.ValueStart, attribute.ValueLength).ToString()
                    );
                    counters.OwnedValues++;
                }
                else
                {
                    value = new CompactExtractionValue(document, attribute.ValueStart, attribute.ValueLength);
                    counters.BorrowedValues++;
                }
            }

            fields[index] = new CompactExtractionField(projection.Field, value);
        }
        row = new CompactExtractionRow(node.Handle, fields);
        return true;
    }

    private static CompactPlanRequirements BuildRequirements(PlanStep[] steps, PlanProjection[] projections)
    {
        var inspected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            if (step.ClassToken is not null)
                inspected.Add("class");
            foreach (var predicate in step.Attributes)
                inspected.Add(predicate.Name);
        }
        foreach (var projection in projections)
            if (projection.Attribute is not null)
                retained.Add(projection.Attribute);
        var materialized = new HashSet<string>(inspected, StringComparer.OrdinalIgnoreCase);
        materialized.UnionWith(retained);
        return new CompactPlanRequirements(
            inspected.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            retained.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            materialized.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            projections.Any(static projection => projection.NormalizedText),
            CompactMetadataOptions.None
        );
    }

    private static ResolvedStep Resolve(PlanStep step, CompactDocument document) =>
        new(
            step.Axis,
            document.ResolveNameId(step.Tag),
            step.ClassToken,
            step.ClassToken is null ? ushort.MaxValue : document.ResolveNameId("class"),
            step.Attributes
                .Select(predicate => new ResolvedAttributePredicate(document.ResolveNameId(predicate.Name), predicate.Value))
                .ToArray()
        );

    private static ResolvedProjection Resolve(PlanProjection projection, CompactDocument document) =>
        new(
            projection.Field,
            projection.Attribute is null ? ushort.MaxValue : document.ResolveNameId(projection.Attribute),
            projection.Required,
            projection.Own,
            projection.NormalizedText
        );

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
                break;
            values = values[(end + 1)..];
        }
        return false;
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var output = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
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
        return output.ToString();
    }

    private sealed class MutableCounters
    {
        public int CandidateNodes;
        public int AttributesInspected;
        public int MatchedNodes;
        public int RowsProduced;
        public int RowsRejected;
        public int BorrowedValues;
        public int OwnedValues;

        public CompactExecutionCounters Snapshot() =>
            new(
                CandidateNodes,
                AttributesInspected,
                MatchedNodes,
                RowsProduced,
                RowsRejected,
                BorrowedValues,
                OwnedValues
            );
    }
}

internal readonly record struct PlanStep(
    CompactPathAxis Axis,
    string Tag,
    string? ClassToken,
    AttributePredicate[] Attributes
);

internal readonly record struct AttributePredicate(string Name, string? Value);

internal readonly record struct PlanProjection(
    string Field,
    string? Attribute,
    bool Required,
    bool Own,
    bool NormalizedText
);

internal readonly record struct ResolvedStep(
    CompactPathAxis Axis,
    ushort TagId,
    string? ClassToken,
    ushort ClassId,
    ResolvedAttributePredicate[] Attributes
);

internal readonly record struct ResolvedAttributePredicate(ushort NameId, string? Value);

internal readonly record struct ResolvedProjection(
    string Field,
    ushort AttributeId,
    bool Required,
    bool Own,
    bool NormalizedText
);
