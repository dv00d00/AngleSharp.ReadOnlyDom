using System.Text;

namespace AngleSharp.ReadOnlyDom.Streaming.Query;

internal static class QueryPlanCompiler
{
    internal static QueryPlan<TState> Compile<TState>(QueryNode<TState> root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return Compile([root.RootNode]);
    }

    internal static QueryPlan<TState> CompileRoots<TState>(IReadOnlyList<QueryNode<TState>> queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (queries.Count == 0)
            throw new ArgumentException("At least one query is required.", nameof(queries));

        var seen = new HashSet<QueryNode<TState>>(ReferenceEqualityComparer.Instance);
        var roots = new List<QueryNode<TState>>(queries.Count);
        foreach (var query in queries)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (seen.Add(query.RootNode))
                roots.Add(query.RootNode);
        }
        return Compile(roots);
    }

    internal static QueryPlan<TState> Compile<TState>(IReadOnlyList<QueryNode<TState>> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count == 0)
            throw new ArgumentException("At least one query root is required.", nameof(roots));

        var sourceNodes = new List<QueryNode<TState>>();
        foreach (var root in roots)
        {
            ArgumentNullException.ThrowIfNull(root);
            if (root.Parent is not null)
                throw new ArgumentException("Every compiled query must be a root query.", nameof(roots));
            AddPreorder(root, sourceNodes);
        }
        if (sourceNodes.Count > 64)
            throw new NotSupportedException("Lexical streaming plans support at most 64 query nodes.");

        var attributeNames = sourceNodes
            .SelectMany(static node =>
                node.Selector.Attributes.Select(static predicate => predicate.Name).Concat(node.ProjectedAttributes)
            )
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var attributeNamesUtf8 = attributeNames.Select(Encoding.UTF8.GetBytes).ToArray();
        if (attributeNames.Length > 64)
            throw new NotSupportedException("Lexical streaming plans support at most 64 required attributes.");
        var attributeIndexes = attributeNames
            .Select(static (name, index) => (name, index))
            .ToDictionary(static pair => pair.name, static pair => pair.index, StringComparer.Ordinal);
        var sourceIndexes = sourceNodes
            .Select(static (node, index) => (node, index))
            .ToDictionary(static pair => pair.node, static pair => pair.index);

        var nodes = new QueryPlanNode<TState>[sourceNodes.Count];
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
            nodes[index] = new QueryPlanNode<TState>(
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
            .Select(static node => Encoding.UTF8.GetString(node.TagNameUtf8))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var tagDispatch = nodes
            .GroupBy(static node => (node.TagHash, node.TagNameUtf8.Length))
            .Select(static group => new CompiledTagDispatch(
                group.Key.TagHash,
                group.Key.Length,
                group.Aggregate(0UL, static (bits, node) => bits | (1UL << node.Index))
            ))
            .OrderBy(static entry => entry.Hash)
            .ThenBy(static entry => entry.Length)
            .ToArray();
        var explanation = new QueryExplanation(
            QueryExecutionModel.LexicalStreaming,
            tags,
            attributeNames,
            nodes.Length,
            24
        );
        return new QueryPlan<TState>(nodes, attributeNames, attributeNamesUtf8, tagDispatch, explanation);
    }

    private static void AddPreorder<TState>(QueryNode<TState> node, List<QueryNode<TState>> nodes)
    {
        nodes.Add(node);
        foreach (var child in node.Children)
            AddPreorder(child, nodes);
    }

    internal static ulong Hash(ReadOnlySpan<byte> value) => Utf8NameHash.ComputeSemantic(value);
}
