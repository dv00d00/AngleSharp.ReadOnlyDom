using System.Text;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

namespace AngleSharp.ReadOnlyDom.Streaming.Query.Execution;

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
        var attributeIdentities = attributeNamesUtf8.Select(CompileAttributeIdentity).ToArray();
        var compactAttributeMask = 0UL;
        for (var index = 0; index < attributeIdentities.Length; index++)
        {
            if (attributeIdentities[index].Length == 0)
                compactAttributeMask |= 1UL << index;
        }
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
            var tagIdentity = CompileTagIdentity(tagName);
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
                tagIdentity.Value,
                tagIdentity.Length,
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

        var tagDispatch = nodes
            .GroupBy(static node => (node.TagIdentity, node.TagIdentityLength))
            .Select(static group => new CompiledTagDispatch(
                group.Key.TagIdentity,
                group.Key.TagIdentityLength,
                group.Aggregate(0UL, static (bits, node) => bits | (1UL << node.Index))
            ))
            .OrderBy(static entry => entry.Identity)
            .ThenBy(static entry => entry.IdentityLength)
            .ToArray();
        return new QueryPlan<TState>(
            nodes,
            attributeNames,
            attributeNamesUtf8,
            attributeIdentities,
            compactAttributeMask,
            tagDispatch
        );
    }

    private static CompiledNameIdentity CompileAttributeIdentity(byte[] name)
    {
        return Utf8HtmlName.TryGetCompactKey(name, out var identity)
            ? new CompiledNameIdentity(identity, 0)
            : new CompiledNameIdentity(0, name.Length);
    }

    private static CompiledNameIdentity CompileTagIdentity(byte[] name)
    {
        if (Utf8HtmlName.TryGetCompactKey(name, out var identity))
            return new CompiledNameIdentity(identity, 0);
        return new CompiledNameIdentity(Utf8NameHash.ComputeSemantic(name), name.Length);
    }

    private static void AddPreorder<TState>(QueryNode<TState> node, List<QueryNode<TState>> nodes)
    {
        nodes.Add(node);
        foreach (var child in node.Children)
            AddPreorder(child, nodes);
    }
}
