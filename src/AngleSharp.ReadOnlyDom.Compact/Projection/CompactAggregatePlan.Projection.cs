using System.Text;
using AngleSharp.ReadOnlyDom.Compact.Arena;
using Microsoft.Extensions.ObjectPool;
using ConstructionArena = AngleSharp.ReadOnlyDom.Compact.Arena.Arena;

namespace AngleSharp.ReadOnlyDom.Compact;

public sealed partial class CompactAggregatePlan
{
    private bool TryProject(
        ConstructionArena arena,
        int scope,
        CompactAggregateExecutionState state,
        out CompactAggregateRow row
    )
    {
        var values = new CompactAggregateField[_fields.Length];
        for (var index = 0; index < _fields.Length; index++)
        {
            var field = _fields[index];
            var projection = field.Projection;
            var target = projection.Selector is null ? scope : FindFirst(arena, scope, projection.Selector, state);
            CompactExtractionValue value = default;
            if (target >= 0)
            {
                value = projection.Kind switch
                {
                    CompactAggregateProjectionKind.Attribute => ProjectAttribute(
                        arena,
                        target,
                        projection.Attribute!,
                        state
                    ),
                    CompactAggregateProjectionKind.NormalizedText => new CompactExtractionValue(
                        NormalizeText(arena, target)
                    ),
                    _ => throw new InvalidOperationException("Unknown aggregate projection."),
                };
            }
            if (field.Required && !value.Exists)
            {
                row = null!;
                return false;
            }
            values[index] = new CompactAggregateField(field.Name, value);
        }
        row = new CompactAggregateRow(values);
        return true;
    }

    private static CompactExtractionValue ProjectAttribute(
        ConstructionArena arena,
        int handle,
        string name,
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
            if (arena.AttributeName(attribute).Memory.Span.Equals(name, StringComparison.OrdinalIgnoreCase))
                return new CompactExtractionValue(arena.AttributeValue(attribute).Memory.ToString());
        }
        return default;
    }

    private static readonly ObjectPool<StringBuilder> TextBuilderPool = ObjectPool.Create(
        new StringBuilderPooledObjectPolicy { InitialCapacity = 256, MaximumRetainedCapacity = 8 * 1024 }
    );

    private static string NormalizeText(ConstructionArena arena, int target)
    {
        var output = TextBuilderPool.Get();
        try
        {
            var pendingSpace = false;
            Append(target);
            return output.ToString();

            void Append(int handle)
            {
                if (arena.Kind(handle) == CompactNodeKind.Text)
                {
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
                for (var child = arena.FirstChild(handle); child >= 0; child = arena.NextSibling(child))
                    Append(child);
            }
        }
        finally
        {
            TextBuilderPool.Return(output);
        }
    }

}
