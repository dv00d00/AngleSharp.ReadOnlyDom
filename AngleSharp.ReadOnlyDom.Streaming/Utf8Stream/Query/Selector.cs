namespace AngleSharp.ReadOnlyDom.Streaming.Utf8Stream.Query;

public sealed class Selector
{
    private readonly List<AttributePredicate> _attributes = [];

    private Selector(string tagName) => TagName = NormalizeName(tagName, nameof(tagName));

    public string TagName { get; }

    internal IReadOnlyList<AttributePredicate> Attributes => _attributes;

    public static Selector Tag(string tagName) => new(tagName);

    public Selector WithId(string value) => WithAttribute("id", value);

    public Selector WithClass(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Any(IsHtmlSpace))
            throw new ArgumentException("A class-token predicate must contain exactly one token.", nameof(token));
        _attributes.Add(
            new AttributePredicate(NormalizeName("class", "name"), AttributePredicateKind.ContainsToken, token)
        );
        return this;
    }

    public Selector WithAttribute(string name)
    {
        _attributes.Add(new AttributePredicate(NormalizeName(name, nameof(name)), AttributePredicateKind.Exists, null));
        return this;
    }

    public Selector WithAttribute(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _attributes.Add(
            new AttributePredicate(NormalizeName(name, nameof(name)), AttributePredicateKind.Equals, value)
        );
        return this;
    }

    internal static string NormalizeName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        foreach (var character in value)
        {
            if (character > 0x7F)
                throw new NotSupportedException(
                    "The streaming query prototype accepts ASCII tag and attribute names only."
                );
        }
        return value.ToLowerInvariant();
    }

    private static bool IsHtmlSpace(char value) => value is ' ' or '\t' or '\n' or '\r' or '\f';
}