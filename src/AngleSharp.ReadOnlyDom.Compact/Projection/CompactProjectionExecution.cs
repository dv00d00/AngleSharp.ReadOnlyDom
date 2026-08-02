using System.Runtime.InteropServices;
using AngleSharp.Common;
using AngleSharp.ReadOnlyDom.Compact.Arena;
using AngleSharp.Text;
using ConstructionArena = AngleSharp.ReadOnlyDom.Compact.Arena.Arena;

namespace AngleSharp.ReadOnlyDom.Compact.Projection;

internal sealed class CompactProjectionDefinition(CompactProjectionPlan plan, bool collectDiagnostics)
    : ICompactConstructionViewDefinition
{
    public ICompactConstructionViewState CreateState(TextSource source)
    {
        return new CompactProjectionExecutionState(
            plan,
            collectDiagnostics ? new CompactProjectionDiagnostics(SourceIdentity(source)) : null
        );
    }

    private static string? SourceIdentity(TextSource source)
    {
        return source.GetUnderlyingTextSource() is StringTextSource text ? text.Text : null;
    }
}

internal sealed class CompactProjectionExecutionState : ICompactConstructionViewState
{
    private readonly CompactProjectionPlan _plan;

    internal CompactProjectionExecutionState(
        CompactProjectionPlan plan,
        CompactProjectionDiagnostics? diagnostics
    )
    {
        _plan = plan;
        Diagnostics = diagnostics;
    }

    internal CompactProjectionDiagnostics? Diagnostics { get; }

    public void SetTokensProcessed(int count)
    {
        Diagnostics?.SetTokensProcessed(count);
    }

    public void NodeMaterialized()
    {
        Diagnostics?.NodeMaterialized();
    }

    public void AttributeRetained(StringOrMemory value)
    {
        Diagnostics?.AttributeRetained(value);
    }

    public void CompleteAttributes(ConstructionArena arena, int handle)
    {
    }

    public StringOrMemory SelectTextValue(StringOrMemory value)
    {
        if (!_plan.Requirements.RetainsText)
            return default;

        Diagnostics?.TextValueRetained(value);
        return value;
    }

    public CompactProjectionResult CreateResult(ConstructionArena arena, int root, int inputBytesConsumed)
    {
        return _plan.Evaluate(arena, root, this, inputBytesConsumed);
    }

    internal void CandidateNode()
    {
        Diagnostics?.CandidateNode();
    }

    internal void MatchedScope()
    {
        Diagnostics?.MatchedScope();
    }

    internal void AttributeInspected()
    {
        Diagnostics?.AttributeInspected();
    }

    internal void RowProduced()
    {
        Diagnostics?.RowProduced();
    }

    internal void RowRejected()
    {
        Diagnostics?.RowRejected();
    }

    internal void NormalizedTextProjected()
    {
        Diagnostics?.NormalizedTextProjected();
    }

    internal CompactProjectionCounters Snapshot(int inputBytesConsumed)
    {
        return Diagnostics?.Snapshot(inputBytesConsumed) ?? default;
    }
}

internal sealed class CompactProjectionDiagnostics
{
    private readonly string? _source;
    private int _attributesInspected;
    private int _attributesRetained;
    private int _candidateNodes;
    private int _matchedScopes;
    private int _nodesMaterialized;
    private int _normalizedTextValuesProjected;
    private int _rowsProduced;
    private int _rowsRejected;
    private int _textValuesRetained;
    private int _tokensProcessed;
    private int _valuesDecoded;

    internal CompactProjectionDiagnostics(string? source)
    {
        _source = source;
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

    public void TextValueRetained(StringOrMemory value)
    {
        _textValuesRetained++;
        ObserveDecoded(value);
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

    public void NormalizedTextProjected()
    {
        _normalizedTextValuesProjected++;
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
            _normalizedTextValuesProjected,
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
