namespace AngleSharp.ReadOnlyDom.Compact.Projection;

/// <summary>
///     How a selector step relates to the step before it.
/// </summary>
internal enum CompactPathAxis
{
    /// <summary>Matches at any depth below the previous step.</summary>
    Descendant,

    /// <summary>Matches only as a direct child of the previous step.</summary>
    Child
}

/// <summary>
///     A path of one or more steps. The last step describes the matched element itself; earlier steps
///     constrain its ancestors, each related to the following step by a <see cref="CompactPathAxis" />.
///     Predicate builders (<see cref="WithId" />, <see cref="WithClass" />, <see cref="WithAttribute" />)
///     always refine the last step.
/// </summary>
public sealed class CompactProjectionSelector
{
    private readonly CompactProjectionSelectorStep[] _steps;

    private CompactProjectionSelector(CompactProjectionSelectorStep[] steps)
    {
        _steps = steps;
        RequiresMatchMemoization = HasMultipleDescendantAxes(steps);
    }

    internal ReadOnlySpan<CompactProjectionSelectorStep> Steps => _steps;
    internal bool RequiresMatchMemoization { get; }

    private CompactProjectionSelectorStep Last => _steps[^1];

    internal ReadOnlySpan<CompactProjectionAttributePredicate> Attributes => Last.Attributes;

    public static CompactProjectionSelector Tag(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        return new CompactProjectionSelector([new CompactProjectionSelectorStep(CompactPathAxis.Descendant, tag)]);
    }

    /// <summary>Adds a step matching at any depth below the current one.</summary>
    public CompactProjectionSelector Descendant(string tag)
    {
        return Append(CompactPathAxis.Descendant, tag);
    }

    /// <summary>Adds a step matching only as a direct child of the current one.</summary>
    public CompactProjectionSelector Child(string tag)
    {
        return Append(CompactPathAxis.Child, tag);
    }

    public CompactProjectionSelector WithId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return ReplaceLast(Last with { Id = id });
    }

    public CompactProjectionSelector WithClass(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        foreach (var character in token)
            if (IsHtmlSpace(character))
                throw new ArgumentException("A class token cannot contain HTML whitespace.", nameof(token));
        return ReplaceLast(Last with { ClassToken = token });
    }

    internal static bool IsHtmlSpace(char value)
    {
        return value is '\t' or '\n' or '\f' or '\r' or ' ';
    }

    public CompactProjectionSelector WithAttribute(string name, string? value = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var attributes = new CompactProjectionAttributePredicate[Last.Attributes.Length + 1];
        Last.Attributes.CopyTo(attributes, 0);
        attributes[^1] = new CompactProjectionAttributePredicate(name, value);
        return ReplaceLast(Last with { Attributes = attributes });
    }

    private CompactProjectionSelector Append(CompactPathAxis axis, string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var steps = new CompactProjectionSelectorStep[_steps.Length + 1];
        _steps.CopyTo(steps, 0);
        steps[^1] = new CompactProjectionSelectorStep(axis, tag);
        return new CompactProjectionSelector(steps);
    }

    private CompactProjectionSelector ReplaceLast(CompactProjectionSelectorStep step)
    {
        var steps = (CompactProjectionSelectorStep[])_steps.Clone();
        steps[^1] = step;
        return new CompactProjectionSelector(steps);
    }

    private static bool HasMultipleDescendantAxes(CompactProjectionSelectorStep[] steps)
    {
        var descendants = 0;
        for (var index = 1; index < steps.Length; index++)
            if (steps[index].Axis == CompactPathAxis.Descendant && ++descendants == 2)
                return true;
        return false;
    }
}

internal readonly record struct CompactProjectionSelectorStep(
    CompactPathAxis Axis,
    string TagName,
    string? Id = null,
    string? ClassToken = null,
    CompactProjectionAttributePredicate[]? AttributePredicates = null
)
{
    public CompactProjectionAttributePredicate[] Attributes { get; init; } = AttributePredicates ?? [];
}

internal readonly record struct CompactProjectionAttributePredicate(string Name, string? Value);