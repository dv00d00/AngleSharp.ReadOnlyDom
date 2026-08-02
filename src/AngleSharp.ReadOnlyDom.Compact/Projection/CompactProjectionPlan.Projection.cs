using System.Text;
using AngleSharp.ReadOnlyDom.Compact.Document;
using Microsoft.Extensions.ObjectPool;
using ConstructionArena = AngleSharp.ReadOnlyDom.Compact.Arena.Arena;

namespace AngleSharp.ReadOnlyDom.Compact.Projection;

public sealed partial class CompactProjectionPlan
{
    /// <summary>
    ///     Projected values are field-sized: attribute values, headings, normalized subtree text.
    ///     A builder is rented per projected value because a many-row plan projects one value per field
    ///     per row. Typical fields fit the initial capacity, article-sized text stays reusable, and
    ///     pathological values are dropped instead of retained by the pool.
    /// </summary>
    private static readonly ObjectPool<StringBuilder> TextBuilderPool = ObjectPool.Create(
        new StringBuilderPooledObjectPolicy { InitialCapacity = 256, MaximumRetainedCapacity = 8 * 1024 }
    );

    private bool TryProject(
        ConstructionArena arena,
        int scope,
        CompactProjectionExecutionState state,
        out CompactProjectionRow row
    )
    {
        var values = new CompactProjectionField[_fields.Length];
        for (var index = 0; index < _fields.Length; index++)
        {
            var field = _fields[index];
            var projection = field.Projection;
            var target = projection.Selector is null ? scope : FindFirst(arena, scope, projection.Selector, state);
            CompactProjectionValue value = default;
            if (target >= 0)
                value = projection.Kind switch
                {
                    CompactFieldProjectionKind.Attribute => ProjectAttribute(
                        arena,
                        target,
                        projection.Attribute!,
                        state
                    ),
                    CompactFieldProjectionKind.NormalizedText => new CompactProjectionValue(
                        NormalizeText(arena, target)
                    ),
                    _ => throw new InvalidOperationException("Unknown field projection.")
                };
            if (field.Required && !value.Exists)
            {
                row = null!;
                return false;
            }

            values[index] = new CompactProjectionField(field.Name, value);
        }

        row = new CompactProjectionRow(values);
        return true;
    }

    private static CompactProjectionValue ProjectAttribute(
        ConstructionArena arena,
        int handle,
        string name,
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
            if (arena.AttributeName(attribute).Memory.Span.Equals(name, StringComparison.OrdinalIgnoreCase))
                return new CompactProjectionValue(arena.AttributeValue(attribute).Memory.ToString());
        }

        return default;
    }

    private static string NormalizeText(ConstructionArena arena, int target)
    {
        var output = TextBuilderPool.Get();
        try
        {
            var pendingSpace = false;
            for (var handle = target; handle >= 0; handle = NextInSubtree(arena, handle, target))
            {
                if (arena.Kind(handle) != CompactNodeKind.Text)
                    continue;

                foreach (var character in arena.Value(handle).Memory.Span)
                {
                    if (char.IsWhiteSpace(character))
                    {
                        pendingSpace = output.Length != 0;
                        continue;
                    }

                    if (pendingSpace)
                    {
                        output.Append(' ');
                        pendingSpace = false;
                    }

                    output.Append(character);
                }
            }

            return output.ToString();
        }
        finally
        {
            TextBuilderPool.Return(output);
        }
    }
}