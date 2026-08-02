using System.Buffers;
using System.Text;
using System.Text.Json;

namespace AngleSharp.ReadOnlyDom.Compact;

/// <summary>
/// A single extracted field value, or the absence of one. Extraction runs during tree construction
/// and the arena is disposed before a result is returned, so every value carries its own storage and
/// stays valid for as long as the caller holds it.
/// </summary>
public readonly struct CompactExtractionValue
{
    private readonly string? _value;

    internal CompactExtractionValue(string value)
    {
        _value = value;
        Exists = true;
    }

    /// <summary>
    /// Distinguishes a field that produced no value from one that produced an empty value.
    /// </summary>
    public bool Exists { get; }

    /// <summary>
    /// The value without materializing a new string; empty when <see cref="Exists"/> is false.
    /// </summary>
    public ReadOnlySpan<char> Span => _value.AsSpan();

    public override string ToString() => _value ?? string.Empty;
}

public readonly record struct CompactAggregateRequirements(
    IReadOnlyList<string> InspectedAttributes,
    IReadOnlyList<string> RetainedAttributes,
    bool RetainsText
);

public readonly record struct CompactAggregateCounters(
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

public readonly record struct CompactAggregateField(string Name, CompactExtractionValue Value);

public sealed class CompactAggregateRow
{
    private readonly CompactAggregateField[] _fields;

    internal CompactAggregateRow(CompactAggregateField[] fields) => _fields = fields;

    public IReadOnlyList<CompactAggregateField> Fields => _fields;

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

public sealed class CompactAggregateResult
{
    private readonly CompactAggregateCardinality _cardinality;

    internal CompactAggregateResult(
        CompactAggregateCardinality cardinality,
        CompactAggregateRow[] rows,
        CompactAggregateCounters counters
    )
    {
        _cardinality = cardinality;
        Rows = rows;
        Counters = counters;
    }

    public IReadOnlyList<CompactAggregateRow> Rows { get; }
    public CompactAggregateCounters Counters { get; }

    public void WriteJson(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (_cardinality == CompactAggregateCardinality.All)
        {
            writer.WriteStartArray();
            foreach (var row in Rows)
                WriteRow(writer, row);
            writer.WriteEndArray();
        }
        else if (Rows.Count == 0)
        {
            writer.WriteNullValue();
        }
        else
        {
            WriteRow(writer, Rows[0]);
        }
    }

    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteJson(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteRow(Utf8JsonWriter writer, CompactAggregateRow row)
    {
        writer.WriteStartObject();
        foreach (var field in row.Fields)
        {
            writer.WritePropertyName(field.Name);
            if (field.Value.Exists)
                writer.WriteStringValue(field.Value.Span);
            else
                writer.WriteNullValue();
        }
        writer.WriteEndObject();
    }
}
