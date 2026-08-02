namespace AngleSharp.ReadOnlyDom.Compact;

public enum CompactAggregateCardinality
{
    First,
    All,
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
