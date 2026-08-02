using AngleSharp.ReadOnlyDom.Compact.Arena;
using ConstructionArena = AngleSharp.ReadOnlyDom.Compact.Arena.Arena;

namespace AngleSharp.ReadOnlyDom.Compact;

public sealed partial class CompactAggregatePlan
{
    internal CompactAggregateResult Evaluate(
        ConstructionArena arena,
        int root,
        CompactAggregateExecutionState state,
        int inputBytesConsumed
    )
    {
        var rows = new List<CompactAggregateRow>();
        Visit(root);
        return new CompactAggregateResult(Cardinality, [.. rows], state.Snapshot(inputBytesConsumed));

        bool Visit(int handle)
        {
            if (arena.Kind(handle) == CompactNodeKind.Element)
            {
                state.CandidateNode();
                if (Matches(arena, handle, _scope, state))
                {
                    state.MatchedScope();
                    if (TryProject(arena, handle, state, out var row))
                    {
                        rows.Add(row);
                        state.RowProduced();
                        if (Cardinality == CompactAggregateCardinality.First)
                            return true;
                    }
                    else
                    {
                        state.RowRejected();
                    }
                }
            }

            for (var child = arena.FirstChild(handle); child >= 0; child = arena.NextSibling(child))
                if (Visit(child))
                    return true;
            return false;
        }
    }

    private static int FindFirst(
        ConstructionArena arena,
        int scope,
        CompactAggregateSelector selector,
        CompactAggregateExecutionState state
    )
    {
        for (var child = arena.FirstChild(scope); child >= 0; child = arena.NextSibling(child))
        {
            if (arena.Kind(child) == CompactNodeKind.Element)
            {
                state.CandidateNode();
                if (Matches(arena, child, selector, state))
                    return child;
            }
            var nested = FindFirst(arena, child, selector, state);
            if (nested >= 0)
                return nested;
        }
        return -1;
    }

    private static bool Matches(
        ConstructionArena arena,
        int handle,
        CompactAggregateSelector selector,
        CompactAggregateExecutionState state
    ) => MatchesChain(arena, handle, selector.Steps, selector.Steps.Length - 1, state, null);

    /// <summary>
    /// Matches the step chain right to left: the candidate must satisfy the last step, then each
    /// earlier step must be satisfied by an ancestor. Descendant steps try every ancestor rather than
    /// the nearest match, so a chain like <c>div &gt;&gt; section &gt;&gt; p</c> still matches when an
    /// intermediate ancestor also carries the tag of an earlier step.
    /// </summary>
    private static bool MatchesChain(
        ConstructionArena arena,
        int handle,
        ReadOnlySpan<CompactAggregateSelectorStep> steps,
        int index,
        CompactAggregateExecutionState state,
        Dictionary<(int Handle, int Step), bool>? memo
    )
    {
        var key = (handle, index);
        if (memo is not null && memo.TryGetValue(key, out var cached))
            return cached;

        if (!MatchesStep(arena, handle, steps[index], state))
            return Store(false);

        if (index == 0)
            return Store(true);

        var parent = arena.Parent(handle);
        if (steps[index].Axis == CompactPathAxis.Child)
            return Store(parent >= 0 && MatchesChain(arena, parent, steps, index - 1, state, memo));

        // Descendant chains can reach the same (ancestor, step) pair through many different
        // backtracking paths. Allocate the cache only after a candidate has matched the rightmost
        // step and actually branches; single-step and child-only selectors remain allocation-free.
        memo ??= new Dictionary<(int Handle, int Step), bool>();
        for (var ancestor = parent; ancestor >= 0; ancestor = arena.Parent(ancestor))
            if (MatchesChain(arena, ancestor, steps, index - 1, state, memo))
                return Store(true);
        return Store(false);

        bool Store(bool result)
        {
            if (memo is not null)
                memo[key] = result;
            return result;
        }
    }

    private static bool MatchesStep(
        ConstructionArena arena,
        int handle,
        in CompactAggregateSelectorStep step,
        CompactAggregateExecutionState state
    )
    {
        if (arena.Kind(handle) != CompactNodeKind.Element)
            return false;
        if (!arena.LocalName(handle).Memory.Span.Equals(step.TagName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (step.Id is not null && !MatchAttribute(arena, handle, "id", step.Id, false, state))
            return false;
        if (step.ClassToken is not null && !MatchAttribute(arena, handle, "class", step.ClassToken, true, state))
            return false;
        foreach (var predicate in step.Attributes)
            if (!MatchAttribute(arena, handle, predicate.Name, predicate.Value, false, state))
                return false;
        return true;
    }

    private static bool MatchAttribute(
        ConstructionArena arena,
        int handle,
        string name,
        string? value,
        bool token,
        CompactAggregateExecutionState state
    )
    {
        for (
            var attribute = arena.FirstAttributeHandle(handle);
            attribute >= 0;
            attribute = arena.NextAttribute(attribute)
        )
        {
            state.AttributeInspected();
            if (!arena.AttributeName(attribute).Memory.Span.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (value is null)
                return true;
            var actual = arena.AttributeValue(attribute).Memory.Span;
            return token ? ContainsToken(actual, value) : actual.SequenceEqual(value);
        }
        return false;
    }

    private static bool ContainsToken(ReadOnlySpan<char> values, ReadOnlySpan<char> wanted)
    {
        while (!values.IsEmpty)
        {
            values = values.TrimStart();
            var end = 0;
            while (end < values.Length && !char.IsWhiteSpace(values[end]))
                end++;
            if (end == values.Length)
                end = -1;
            var token = end < 0 ? values : values[..end];
            if (token.SequenceEqual(wanted))
                return true;
            if (end < 0)
                return false;
            values = values[(end + 1)..];
        }
        return false;
    }

    /// <summary>
    /// Projected values are field-sized: attribute values, headings, normalized subtree text.
    /// A builder is rented per projected value rather than allocated, because a many-rows plan projects
    /// one value per field per row. 256 chars covers typical fields without growing; 8K chars stays
    /// retained so article-sized text keeps reusing a builder, while pathological values are dropped
    /// instead of hoarded.
}
