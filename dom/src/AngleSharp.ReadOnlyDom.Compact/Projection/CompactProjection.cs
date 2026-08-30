namespace AngleSharp.ReadOnlyDom.Compact.Projection;

internal enum CompactProjectionCardinality
{
    First,
    All,
}

public static class CompactProjection
{
    public static CompactProjectionPlanBuilder First(CompactProjectionSelector selector)
    {
        return new CompactProjectionPlanBuilder(selector, CompactProjectionCardinality.First);
    }

    public static CompactProjectionPlanBuilder ForEach(CompactProjectionSelector selector)
    {
        return new CompactProjectionPlanBuilder(selector, CompactProjectionCardinality.All);
    }
}

public sealed class CompactProjectionPlanBuilder
{
    private readonly CompactProjectionCardinality _cardinality;
    private readonly List<CompactProjectionFieldDefinition> _fields = [];
    private readonly CompactProjectionSelector _scope;

    internal CompactProjectionPlanBuilder(CompactProjectionSelector selector, CompactProjectionCardinality cardinality)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _scope = selector;
        _cardinality = cardinality;
    }

    public CompactProjectionPlanBuilder Field(string name, CompactFieldProjection projection, bool required = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(projection);
        if (_fields.Any(field => string.Equals(field.Name, name, StringComparison.Ordinal)))
            throw new ArgumentException($"Field '{name}' already exists.", nameof(name));
        _fields.Add(new CompactProjectionFieldDefinition(name, projection, required));
        return this;
    }

    public CompactProjectionPlan Compile()
    {
        if (_fields.Count == 0)
            throw new InvalidOperationException("At least one projection field is required.");
        return new CompactProjectionPlan(_scope, _cardinality, [.. _fields]);
    }
}
