using System.Text;

namespace AngleSharp.ReadOnlyDom.Streaming.Utf8Stream.Query;

internal static class QueryCompiler
{
    public static QueryPlan<TState> Compile<TState>(QueryNode<TState> root)
    {
        var sourceNodes = new List<QueryNode<TState>>();
        AddPreorder(root, sourceNodes);
        if (sourceNodes.Count > 64)
            throw new NotSupportedException("StreamingOnly plans support at most 64 query nodes.");

        var attributeNames = sourceNodes
            .SelectMany(static node =>
                node.Selector.Attributes.Select(static predicate => predicate.Name).Concat(node.ProjectedAttributes)
            )
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var attributeNameUtf8 = attributeNames.Select(Encoding.UTF8.GetBytes).ToArray();
        if (attributeNames.Length > 64)
            throw new NotSupportedException("StreamingOnly plans support at most 64 required attributes.");
        var attributeIndexes = attributeNames
            .Select(static (name, index) => (name, index))
            .ToDictionary(static pair => pair.name, static pair => pair.index, StringComparer.Ordinal);
        var sourceIndexes = sourceNodes
            .Select(static (node, index) => (node, index))
            .ToDictionary(static pair => pair.node, static pair => pair.index);

        var nodes = new CompiledQueryNode<TState>[sourceNodes.Count];
        for (var index = 0; index < sourceNodes.Count; index++)
        {
            var source = sourceNodes[index];
            var tagName = Encoding.UTF8.GetBytes(source.Selector.TagName);
            var predicates = source
                .Selector.Attributes.Select(predicate => new CompiledAttributePredicate(
                    attributeIndexes[predicate.Name],
                    predicate.Kind,
                    predicate.Value is null ? null : Encoding.UTF8.GetBytes(predicate.Value)
                ))
                .ToArray();
            var requiredAttributeBits = source
                .Selector.Attributes.Select(static predicate => predicate.Name)
                .Concat(source.ProjectedAttributes)
                .Aggregate(0UL, (bits, name) => bits | (1UL << attributeIndexes[name]));
            var completedAttributeNames = source.CompletedHandler is null
                ? []
                : source
                    .Selector.Attributes.Select(static predicate => predicate.Name)
                    .Concat(source.ProjectedAttributes)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            nodes[index] = new CompiledQueryNode<TState>(
                index,
                source.Parent is null ? -1 : sourceIndexes[source.Parent],
                source.Relation,
                tagName,
                Hash(tagName),
                requiredAttributeBits,
                predicates,
                source.StartHandler,
                source.TextHandler,
                source.EndHandler,
                source.CompletedHandler,
                source.CompletedTextMode,
                completedAttributeNames.Select(name => attributeIndexes[name]).ToArray()
            );
        }

        var tags = nodes
            .Select(static node => Encoding.UTF8.GetString(node.TagName))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var explanation = new QueryExplanation("StreamingOnly", tags, attributeNames, nodes.Length, 24, false, null);
        return new QueryPlan<TState>(nodes, attributeNames, attributeNameUtf8, explanation);
    }

    private static void AddPreorder<TState>(QueryNode<TState> node, List<QueryNode<TState>> nodes)
    {
        nodes.Add(node);
        foreach (var child in node.Children)
            AddPreorder(child, nodes);
    }

    internal static ulong Hash(ReadOnlySpan<byte> value)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }
        return hash;
    }
}