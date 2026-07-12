using AngleSharp.Common;
using AngleSharp.ReadOnlyDom.Html;

namespace AngleSharp.ReadOnlyDom.CompactPrototype;

public static class CompactDomBuilder
{
    public static CompactDocument Build(
        IReadOnlyDocument document,
        CompactMetadataOptions options = CompactMetadataOptions.None
    ) =>
        Build(
            document,
            new CompactDomOptions
            {
                ParentLinks = options.HasFlag(CompactMetadataOptions.ParentLinks),
                SourceLocationIndexMode = options.HasFlag(CompactMetadataOptions.SourceLocations)
                    ? CompactIndexMode.Dense
                    : CompactIndexMode.None,
            }
        );

    public static CompactDocument Build(IReadOnlyDocument document, CompactDomOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        var builder = new Builder(options, Measure(document));
        builder.Add(document, -1);
        return builder.Finish();
    }

    private static Size Measure(IReadOnlyNode node)
    {
        var content = GetContent(node);
        var size = new Size(1, 0, content.Length == 0 ? 0 : 1);
        if (node is IReadOnlyElement element)
        {
            foreach (var attribute in element.Attributes)
                size += new Size(0, 1, attribute.Value.Length == 0 ? 0 : 1);
        }

        var children = node is IReadOnlyTemplateElement template ? template.Content : node.ChildNodes;
        foreach (var child in children)
            size += Measure(child);
        return size;
    }

    private static StringOrMemory GetContent(IReadOnlyNode node) =>
        node switch
        {
            IReadOnlyTextNode text => text.Content,
            IReadOnlyCommentNode comment => comment.Content,
            IReadOnlyProcessingInstructionNode instruction => instruction.Content,
            _ => StringOrMemory.Empty,
        };

    private sealed class Builder
    {
        private readonly CompactDomOptions _options;
        private readonly CompactNode[] _nodes;
        private readonly CompactAttribute[] _attributes;
        private readonly ReadOnlyMemory<char>[] _values;
        private readonly Dictionary<StringOrMemory, ushort> _nameIds = [];
        private readonly List<string> _names = [];
        private readonly int[]? _parents;
        private readonly List<(int Handle, CompactSourceLocation Value)>? _sources;
        private int _nodeIndex;
        private int _attributeIndex;
        private int _valueIndex;

        public Builder(CompactDomOptions options, Size size)
        {
            _options = options;
            _nodes = new CompactNode[size.Nodes];
            _attributes = new CompactAttribute[size.Attributes];
            _values = new ReadOnlyMemory<char>[size.Values];
            if (options.ParentLinks)
                _parents = new int[size.Nodes];
            if (options.SourceLocationIndexMode != CompactIndexMode.None)
                _sources = [];
        }

        public int Add(IReadOnlyNode node, int parent)
        {
            var handle = _nodeIndex++;
            if (_parents is not null)
                _parents[handle] = parent;
            if (_sources is not null && TryGetSource(node, out var source))
                _sources.Add((handle, source));

            var firstAttribute = _attributeIndex;
            ushort attributeCount = 0;
            if (node is IReadOnlyElement element)
            {
                foreach (var attribute in element.Attributes)
                {
                    _attributes[_attributeIndex++] = new CompactAttribute(
                        GetNameId(attribute.Name),
                        AddValue(attribute.Value),
                        attribute.Value.Length
                    );
                    attributeCount++;
                }
            }

            var content = GetContent(node);
            var valueIndex = AddValue(content);
            var children = node is IReadOnlyTemplateElement template ? template.Content : node.ChildNodes;
            var firstChild = -1;
            var previousChild = -1;
            foreach (var child in children)
            {
                var childHandle = Add(child, handle);
                if (firstChild < 0)
                    firstChild = childHandle;
                if (previousChild >= 0)
                    SetNextSibling(previousChild, childHandle);
                previousChild = childHandle;
            }

            _nodes[handle] = new CompactNode(
                firstChild,
                -1,
                firstAttribute,
                valueIndex,
                content.Length,
                GetNameId(node.NodeName),
                attributeCount,
                node.Flags,
                GetKind(node)
            );
            return handle;
        }

        public CompactDocument Finish() => new(_nodes, _attributes, _names, _values, _parents, CreateSourceIndex());

        private INodePayloadIndex<CompactSourceLocation>? CreateSourceIndex()
        {
            if (_sources is null)
                return null;

            return _options.SourceLocationIndexMode switch
            {
                CompactIndexMode.Dense => CreateDenseSourceIndex(),
                CompactIndexMode.Sparse => new SparseNodePayloadIndex<CompactSourceLocation>(
                    _sources.Select(item => item.Handle).ToArray(),
                    _sources.Select(item => item.Value).ToArray()
                ),
                CompactIndexMode.Dictionary => new DictionaryNodePayloadIndex<CompactSourceLocation>(
                    _sources.ToDictionary(item => item.Handle, item => item.Value)
                ),
                _ => throw new ArgumentOutOfRangeException(),
            };
        }

        private INodePayloadIndex<CompactSourceLocation> CreateDenseSourceIndex()
        {
            var values = new CompactSourceLocation[_nodes.Length];
            var present = new bool[_nodes.Length];
            foreach (var item in _sources!)
            {
                values[item.Handle] = item.Value;
                present[item.Handle] = true;
            }
            return new DenseNodePayloadIndex<CompactSourceLocation>(values, present);
        }

        private void SetNextSibling(int handle, int sibling)
        {
            var node = _nodes[handle];
            _nodes[handle] = new CompactNode(
                node.FirstChild,
                sibling,
                node.FirstAttribute,
                node.ValueStart,
                node.ValueLength,
                node.NameId,
                node.AttributeCount,
                node.Flags,
                node.Kind
            );
        }

        private int AddValue(StringOrMemory value)
        {
            if (value.Length == 0)
                return -1;
            var index = _valueIndex++;
            _values[index] = value.Memory;
            return index;
        }

        private ushort GetNameId(StringOrMemory name)
        {
            if (_nameIds.TryGetValue(name, out var id))
                return id;
            if (_names.Count == ushort.MaxValue)
                throw new InvalidOperationException("The compact prototype supports at most 65,535 distinct names.");
            id = (ushort)_names.Count;
            _nameIds.Add(name, id);
            _names.Add(name.ToString());
            return id;
        }

        private static CompactNodeKind GetKind(IReadOnlyNode node) =>
            node switch
            {
                IReadOnlyDocument => CompactNodeKind.Document,
                IReadOnlyElement => CompactNodeKind.Element,
                IReadOnlyProcessingInstructionNode => CompactNodeKind.ProcessingInstruction,
                IReadOnlyCommentNode => CompactNodeKind.Comment,
                IReadOnlyTextNode => CompactNodeKind.Text,
                _ => CompactNodeKind.Other,
            };

        private static bool TryGetSource(IReadOnlyNode node, out CompactSourceLocation location)
        {
            if (node is IReadOnlyElement { SourceReference: { } source })
            {
                var position = source.Position;
                location = new CompactSourceLocation(position.Index, (ushort)position.Line, (ushort)position.Column);
                return true;
            }

            location = default;
            return false;
        }
    }

    private readonly record struct Size(int Nodes, int Attributes, int Values)
    {
        public static Size operator +(Size left, Size right) =>
            new(left.Nodes + right.Nodes, left.Attributes + right.Attributes, left.Values + right.Values);
    }
}
