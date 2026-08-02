using System.Runtime.InteropServices;
using AngleSharp.Common;
using AngleSharp.ReadOnlyDom.Compact.Arena;
using AngleSharp.Text;
using ConstructionArena = AngleSharp.ReadOnlyDom.Compact.Arena.Arena;

namespace AngleSharp.ReadOnlyDom.Compact.Projection;

internal sealed class CompactProjectionDefinition(CompactProjectionPlan plan) : ICompactConstructionViewDefinition
{
    public ICompactConstructionViewState CreateState(TextSource source)
    {
        return new CompactProjectionExecutionState(SourceIdentity(source), plan);
    }

    private static string? SourceIdentity(TextSource source)
    {
        return source.GetUnderlyingTextSource() is StringTextSource text ? text.Text : null;
    }
}

internal sealed class CompactProjectionExecutionState : ICompactConstructionViewState
{
    private readonly CompactProjectionPlan _plan;
    private readonly string? _source;
    private int _attributesInspected;
    private int _attributesRetained;
    private int _candidateNodes;
    private int _matchedScopes;
    private int _nodesMaterialized;
    private int _rowsProduced;
    private int _rowsRejected;
    private int _textValuesRetained;
    private int _tokensProcessed;
    private int _valuesDecoded;

    public CompactProjectionExecutionState(string? source, CompactProjectionPlan plan)
    {
        _source = source;
        _plan = plan;
    }

    public void SetTokensProcessed(int count)
    {
        _tokensProcessed = count;
    }

    public void NodeMaterialized()
    {
        _nodesMaterialized++;
    }

    public void AttributeRetained(StringOrMemory value)
    {
        _attributesRetained++;
        ObserveDecoded(value);
    }

    public void CompleteAttributes(ConstructionArena arena, int handle)
    {
    }

    public StringOrMemory SelectTextValue(StringOrMemory value)
    {
        if (!_plan.Requirements.RetainsText)
            return default;

        _textValuesRetained++;
        ObserveDecoded(value);
        return value;
    }

    public void CandidateNode()
    {
        _candidateNodes++;
    }

    public void MatchedScope()
    {
        _matchedScopes++;
    }

    public void AttributeInspected()
    {
        _attributesInspected++;
    }

    public void RowProduced()
    {
        _rowsProduced++;
    }

    public void RowRejected()
    {
        _rowsRejected++;
    }

    public CompactProjectionResult CreateResult(ConstructionArena arena, int root, int inputBytesConsumed)
    {
        return _plan.Evaluate(arena, root, this, inputBytesConsumed);
    }

    public CompactProjectionCounters Snapshot(int inputBytesConsumed)
    {
        return new CompactProjectionCounters(
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
    }

    private void ObserveDecoded(StringOrMemory value)
    {
        if (
            !MemoryMarshal.TryGetString(value.Memory, out var backing, out _, out _)
            || !ReferenceEquals(backing, _source)
        )
            _valuesDecoded++;
    }
}