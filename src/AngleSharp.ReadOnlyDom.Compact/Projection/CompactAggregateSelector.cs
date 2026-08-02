namespace AngleSharp.ReadOnlyDom.Compact;

/// <summary>
/// How a selector step relates to the step before it.
/// </summary>
public enum CompactPathAxis
{
    /// <summary>Matches at any depth below the previous step.</summary>
    Descendant,

    /// <summary>Matches only as a direct child of the previous step.</summary>
    Child,
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

internal readonly record struct CompactAggregateAttributePredicate(string Name, string? Value);
