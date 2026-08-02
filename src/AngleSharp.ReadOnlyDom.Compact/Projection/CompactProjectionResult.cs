namespace AngleSharp.ReadOnlyDom.Compact.Projection;

/// <summary>
///     A single extracted field value, or the absence of one. Projection is evaluated at EOF over the
///     temporary construction arena, which is disposed before a result is returned. Every value therefore
///     carries its own storage and stays valid for as long as the caller holds it.
/// </summary>
public readonly struct CompactProjectionValue
{
    private readonly string? _value;

    internal CompactProjectionValue(string value)
    {
        _value = value;
        Exists = true;
    }

    /// <summary>
    ///     Distinguishes a field that produced no value from one that produced an empty value.
    /// </summary>
    public bool Exists { get; }

    /// <summary>
    ///     The value without materializing a new string; empty when <see cref="Exists" /> is false.
    /// </summary>
    public ReadOnlySpan<char> Span => _value.AsSpan();

    public override string ToString()
    {
        return _value ?? string.Empty;
    }
}

internal readonly record struct CompactProjectionRequirements(
    IReadOnlyList<string> InspectedAttributes,
    IReadOnlyList<string> RetainedAttributes,
    bool RetainsText
);

internal readonly record struct CompactProjectionCounters(
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

public readonly record struct CompactProjectionField(string Name, CompactProjectionValue Value);

public sealed class CompactProjectionRow
{
    private readonly CompactProjectionField[] _fields;

    internal CompactProjectionRow(CompactProjectionField[] fields)
    {
        _fields = fields;
    }

    public IReadOnlyList<CompactProjectionField> Fields => _fields;

    public CompactProjectionValue this[string name]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(name);
            foreach (var field in _fields)
                if (string.Equals(field.Name, name, StringComparison.Ordinal))
                    return field.Value;
            throw new KeyNotFoundException($"The projection does not define a field named '{name}'.");
        }
    }
}

public sealed class CompactProjectionResult
{
    internal CompactProjectionResult(CompactProjectionRow[] rows, CompactProjectionCounters counters)
    {
        Rows = rows;
        Counters = counters;
    }

    public IReadOnlyList<CompactProjectionRow> Rows { get; }
    internal CompactProjectionCounters Counters { get; }
}