namespace AngleSharp.ReadOnlyDom.Compact.Projection;

internal enum CompactFieldProjectionKind
{
    Attribute,
    NormalizedText
}

public sealed class CompactFieldProjection
{
    private CompactFieldProjection(
        CompactFieldProjectionKind kind,
        CompactProjectionSelector? selector,
        string? attribute
    )
    {
        Kind = kind;
        Selector = selector;
        Attribute = attribute;
    }

    internal CompactFieldProjectionKind Kind { get; }
    internal CompactProjectionSelector? Selector { get; }
    internal string? Attribute { get; }

    public static CompactFieldProjection SelfNormalizedText()
    {
        return new CompactFieldProjection(CompactFieldProjectionKind.NormalizedText, null, null);
    }

    public static CompactFieldProjection FirstNormalizedText(CompactProjectionSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return new CompactFieldProjection(CompactFieldProjectionKind.NormalizedText, selector, null);
    }

    public static CompactFieldProjection SelfAttribute(string attribute)
    {
        return AttributeProjection(null, attribute);
    }

    public static CompactFieldProjection FirstAttribute(CompactProjectionSelector selector, string attribute)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return AttributeProjection(selector, attribute);
    }

    private static CompactFieldProjection AttributeProjection(CompactProjectionSelector? selector, string attribute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        return new CompactFieldProjection(CompactFieldProjectionKind.Attribute, selector, attribute);
    }
}

internal readonly record struct CompactProjectionFieldDefinition(
    string Name,
    CompactFieldProjection Projection,
    bool Required
);