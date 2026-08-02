namespace AngleSharp.ReadOnlyDom.Compact;

public enum CompactAggregateProjectionKind
{
    Attribute,
    NormalizedText,
}

public sealed class CompactAggregateProjection
{
    private CompactAggregateProjection(
        CompactAggregateProjectionKind kind,
        CompactAggregateSelector? selector,
        string? attribute
    )
    {
        Kind = kind;
        Selector = selector;
        Attribute = attribute;
    }

    public CompactAggregateProjectionKind Kind { get; }
    public CompactAggregateSelector? Selector { get; }
    public string? Attribute { get; }

    public static CompactAggregateProjection SelfNormalizedText() =>
        new(CompactAggregateProjectionKind.NormalizedText, null, null);

    public static CompactAggregateProjection FirstNormalizedText(CompactAggregateSelector selector) =>
        new(CompactAggregateProjectionKind.NormalizedText, selector, null);

    public static CompactAggregateProjection SelfAttribute(string attribute) => AttributeProjection(null, attribute);

    public static CompactAggregateProjection FirstAttribute(CompactAggregateSelector selector, string attribute) =>
        AttributeProjection(selector, attribute);

    private static CompactAggregateProjection AttributeProjection(CompactAggregateSelector? selector, string attribute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        return new CompactAggregateProjection(CompactAggregateProjectionKind.Attribute, selector, attribute);
    }
}

internal readonly record struct CompactAggregateFieldDefinition(
    string Name,
    CompactAggregateProjection Projection,
    bool Required
);
