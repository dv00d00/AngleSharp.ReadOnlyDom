using AngleSharp.ReadOnlyDom.Compact.Document;
using ConstructionArena = AngleSharp.ReadOnlyDom.Compact.Arena.Arena;

namespace AngleSharp.ReadOnlyDom.Compact.Projection;

public sealed partial class CompactProjectionPlan
{
    internal CompactProjectionResult Evaluate(
        ConstructionArena arena,
        int root,
        CompactProjectionExecutionState state,
        int inputBytesConsumed
    )
    {
        var rows = new List<CompactProjectionRow>();
        for (var handle = root; handle >= 0; handle = NextInSubtree(arena, handle, root))
        {
            if (arena.Kind(handle) != CompactNodeKind.Element)
                continue;

            state.CandidateNode();
            if (!Matches(arena, handle, _scope, state))
                continue;

            state.MatchedScope();
            if (TryProject(arena, handle, state, out var row))
            {
                rows.Add(row);
                state.RowProduced();
                if (Cardinality == CompactProjectionCardinality.First)
                    break;
            }
            else
            {
                state.RowRejected();
            }
        }

        return new CompactProjectionResult([.. rows], state.Snapshot(inputBytesConsumed));
    }

    private static int FindFirst(
        ConstructionArena arena,
        int scope,
        CompactProjectionSelector selector,
        CompactProjectionExecutionState state
    )
    {
        for (var handle = arena.FirstChild(scope); handle >= 0; handle = NextInSubtree(arena, handle, scope))
        {
            if (arena.Kind(handle) != CompactNodeKind.Element)
                continue;

            state.CandidateNode();
            if (Matches(arena, handle, selector, state))
                return handle;
        }

        return -1;
    }

    private static int NextInSubtree(ConstructionArena arena, int handle, int root)
    {
        var child = arena.FirstChild(handle);
        if (child >= 0)
            return child;

        while (handle != root)
        {
            var sibling = arena.NextSibling(handle);
            if (sibling >= 0)
                return sibling;
            handle = arena.Parent(handle);
        }

        return -1;
    }

    private static bool Matches(
        ConstructionArena arena,
        int handle,
        CompactProjectionSelector selector,
        CompactProjectionExecutionState state
    )
    {
        return MatchesChain(
            arena,
            handle,
            selector.Steps,
            selector.Steps.Length - 1,
            state,
            selector.RequiresMatchMemoization,
            null
        );
    }

    /// <summary>
    ///     Matches the step chain right to left: the candidate must satisfy the last step, then each
    ///     earlier step must be satisfied by an ancestor. Descendant steps try every ancestor rather than
    ///     the nearest match, so a chain like <c>div &gt;&gt; section &gt;&gt; p</c> still matches when an
    ///     intermediate ancestor also carries the tag of an earlier step.
    /// </summary>
    private static bool MatchesChain(
        ConstructionArena arena,
        int handle,
        ReadOnlySpan<CompactProjectionSelectorStep> steps,
        int index,
        CompactProjectionExecutionState state,
        bool useMemoization,
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
            return Store(parent >= 0 && MatchesChain(arena, parent, steps, index - 1, state, useMemoization, memo));

        // Descendant chains can reach the same (ancestor, step) pair through many different
        // backtracking paths when the selector contains at least two descendant axes. Allocate the
        // cache only after a candidate has matched and actually branches; simpler selectors remain
        // allocation-free.
        if (useMemoization)
            memo ??= new Dictionary<(int Handle, int Step), bool>();
        for (var ancestor = parent; ancestor >= 0; ancestor = arena.Parent(ancestor))
            if (MatchesChain(arena, ancestor, steps, index - 1, state, useMemoization, memo))
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
        in CompactProjectionSelectorStep step,
        CompactProjectionExecutionState state
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
        CompactProjectionExecutionState state
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
            var start = 0;
            while (start < values.Length && HtmlClassToken.IsSpace(values[start]))
                start++;
            values = values[start..];
            if (values.IsEmpty)
                return false;

            var end = 0;
            while (end < values.Length && !HtmlClassToken.IsSpace(values[end]))
                end++;
            if (values[..end].SequenceEqual(wanted))
                return true;
            values = values[end..];
        }

        return false;
    }
}
