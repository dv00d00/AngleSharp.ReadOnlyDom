using System.Runtime.InteropServices;
using AngleSharp.Common;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Compact.Arena;
using AngleSharp.Text;
using ConstructionArena = AngleSharp.ReadOnlyDom.Compact.Arena.Arena;

namespace AngleSharp.ReadOnlyDom.Compact;

internal sealed class CompactAggregateDefinition(CompactAggregatePlan plan) : ICompactConstructionViewDefinition
{
    public ICompactConstructionViewState CreateState(TextSource source) =>
        new CompactAggregateExecutionState(SourceIdentity(source), plan);

    private static string? SourceIdentity(TextSource source) =>
        source.GetUnderlyingTextSource() is StringTextSource text ? text.Text : null;
}

internal sealed class CompactAggregateExecutionState : ICompactConstructionViewState
{
    private readonly string? _source;
    private readonly CompactAggregatePlan _plan;
    private int _tokensProcessed;
    private int _nodesMaterialized;
    private int _candidateNodes;
    private int _matchedScopes;
    private int _attributesInspected;
    private int _attributesRetained;
    private int _textValuesRetained;
    private int _valuesDecoded;
    private int _rowsProduced;
    private int _rowsRejected;

    public CompactAggregateExecutionState(string? source, CompactAggregatePlan plan)
    {
        _source = source;
        _plan = plan;
    }

    public void SetTokensProcessed(int count) => _tokensProcessed = count;

    public void NodeMaterialized() => _nodesMaterialized++;

    public void CandidateNode() => _candidateNodes++;

    public void MatchedScope() => _matchedScopes++;

    public void AttributeInspected() => _attributesInspected++;

    public void RowProduced() => _rowsProduced++;

    public void RowRejected() => _rowsRejected++;

    public void AttributeRetained(StringOrMemory value)
    {
        _attributesRetained++;
        ObserveDecoded(value);
    }

    public void CompleteAttributes(ConstructionArena arena, int handle) { }

    public StringOrMemory SelectTextValue(StringOrMemory value)
    {
        _textValuesRetained++;
        ObserveDecoded(value);
        return value;
    }

    public CompactAggregateResult CreateResult(ConstructionArena arena, int root, int inputBytesConsumed) =>
        _plan.Evaluate(arena, root, this, inputBytesConsumed);

    public CompactAggregateCounters Snapshot(int inputBytesConsumed) =>
        new(
            _tokensProcessed,
            _nodesMaterialized,
            _candidateNodes,
            _matchedScopes,
            _attributesInspected,
            _attributesRetained,
            _textValuesRetained,
            _valuesDecoded,
            _rowsProduced,
            _rowsRejected,
            inputBytesConsumed
        );

    private void ObserveDecoded(StringOrMemory value)
    {
        if (
            !MemoryMarshal.TryGetString(value.Memory, out var backing, out _, out _)
            || !ReferenceEquals(backing, _source)
        )
            _valuesDecoded++;
    }
}
