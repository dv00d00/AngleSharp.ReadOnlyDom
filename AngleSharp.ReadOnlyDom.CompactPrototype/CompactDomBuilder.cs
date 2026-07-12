using AngleSharp.Common;
using AngleSharp.ReadOnlyDom.Html;

namespace AngleSharp.ReadOnlyDom.CompactPrototype;

public static class CompactDomBuilder
{
    public static CompactDocument Build(
        IReadOnlyDocument document,
        CompactMetadataOptions options = CompactMetadataOptions.None
    )
    {
        var builder = new Builder(options, Measure(document));
        builder.Add(document, -1);
        return builder.Finish();
    }

    private static Size Measure(IReadOnlyNode node)
    {
        var size = new Size(1, 0, GetContentLength(node));
        if (node is IReadOnlyElement element)
        {
            foreach (var attribute in element.Attributes)
                size += new Size(0, 1, attribute.Value.Length);
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

    private static int GetContentLength(IReadOnlyNode node) =>
        node switch
        {
            IReadOnlyTextNode text => text.Content.Length,
            IReadOnlyCommentNode comment => comment.Content.Length,
            IReadOnlyProcessingInstructionNode instruction => instruction.Content.Length,
            _ => 0,
        };

    private sealed class Builder
    {
        private readonly CompactNode[] _nodes;
        private readonly CompactAttribute[] _attributes;
        private readonly Dictionary<StringOrMemory, ushort> _nameIds = [];
        private readonly List<string> _names = [];
        private readonly char[] _text;
        private readonly int[]? _parents;
        private readonly CompactSourceLocation[]? _sources;
        private int _nodeIndex;
        private int _attributeIndex;
        private int _textIndex;

        public Builder(CompactMetadataOptions options, Size size)
        {
            _nodes = new CompactNode[size.Nodes];
            _attributes = new CompactAttribute[size.Attributes];
            _text = new char[size.TextLength];
            if (options.HasFlag(CompactMetadataOptions.ParentLinks))
                _parents = new int[size.Nodes];
            if (options.HasFlag(CompactMetadataOptions.SourceLocations))
                _sources = new CompactSourceLocation[size.Nodes];
        }

        public int Add(IReadOnlyNode node, int parent)
        {
            var handle = _nodeIndex++;
            if (_parents is not null)
                _parents[handle] = parent;
            if (_sources is not null)
                _sources[handle] = GetSource(node);

            var firstAttribute = _attributeIndex;
            ushort attributeCount = 0;
            if (node is IReadOnlyElement element)
            {
                foreach (var attribute in element.Attributes)
                {
                    var attributeValue = AddText(attribute.Value);
                    _attributes[_attributeIndex++] = new CompactAttribute(
                        GetNameId(attribute.Name),
                        attributeValue.Start,
                        attributeValue.Length
                    );
                    attributeCount++;
                }
            }

            var value = AddText(GetContent(node));
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
                value.Start,
                value.Length,
                GetNameId(node.NodeName),
                attributeCount,
                node.Flags,
                GetKind(node)
            );
            return handle;
        }

        public CompactDocument Finish() => new(_nodes, _attributes, _names.ToArray(), _text, _parents, _sources);

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

        private (int Start, int Length) AddText(StringOrMemory value)
        {
            if (value.Length == 0)
                return (-1, 0);
            var start = _textIndex;
            value.Memory.Span.CopyTo(_text.AsSpan(_textIndex));
            _textIndex += value.Length;
            return (start, value.Length);
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

        private static CompactSourceLocation GetSource(IReadOnlyNode node)
        {
            if (node is not IReadOnlyElement { SourceReference: { } source })
                return new CompactSourceLocation(-1, 0, 0);
            var position = source.Position;
            return new CompactSourceLocation(position.Index, (ushort)position.Line, (ushort)position.Column);
        }
    }

    private readonly record struct Size(int Nodes, int Attributes, int TextLength)
    {
        public static Size operator +(Size left, Size right) =>
            new(left.Nodes + right.Nodes, left.Attributes + right.Attributes, left.TextLength + right.TextLength);
    }
}
