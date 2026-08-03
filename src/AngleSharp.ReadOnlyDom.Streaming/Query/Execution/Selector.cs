namespace AngleSharp.ReadOnlyDom.Streaming.Query.Execution;

internal sealed class Selector
{
    private readonly List<AttributePredicate> _attributes = [];

    private Selector(string tagName) => TagName = NormalizeTagName(tagName, nameof(tagName));

    internal string TagName { get; }

    internal IReadOnlyList<AttributePredicate> Attributes => _attributes;

    internal static Selector Tag(string tagName) => new(tagName);

    internal Selector WithId(string value) => WithAttribute("id", value);

    internal Selector WithClass(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Any(IsHtmlSpace))
            throw new ArgumentException("A class-token predicate must contain exactly one token.", nameof(token));
        _attributes.Add(
            new AttributePredicate(NormalizeAttributeName("class", "name"), AttributePredicateKind.ContainsToken, token)
        );
        return this;
    }

    internal Selector WithAttribute(string name)
    {
        _attributes.Add(
            new AttributePredicate(NormalizeAttributeName(name, nameof(name)), AttributePredicateKind.Exists, null)
        );
        return this;
    }

    internal Selector WithAttribute(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _attributes.Add(
            new AttributePredicate(NormalizeAttributeName(name, nameof(name)), AttributePredicateKind.Equals, value)
        );
        return this;
    }

    internal static string NormalizeTagName(string value, string parameterName) =>
        NormalizeName(value, parameterName, rejectEquals: false);

    internal static string NormalizeAttributeName(string value, string parameterName) =>
        NormalizeName(value, parameterName, rejectEquals: true);

    private static string NormalizeName(string value, string parameterName, bool rejectEquals)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        foreach (var character in value)
        {
            if (character > 0x7F)
                throw new NotSupportedException(
                    "The streaming query prototype accepts ASCII tag and attribute names only."
                );
            if (character <= 0x20 || character is '/' or '>' or (char)0x7F || (rejectEquals && character == '='))
                throw new ArgumentException(
                    "The name contains a control character or HTML tokenizer delimiter.",
                    parameterName
                );
        }
        return value.ToLowerInvariant();
    }

    private static bool IsHtmlSpace(char value) => value is ' ' or '\t' or '\n' or '\r' or '\f';
}
