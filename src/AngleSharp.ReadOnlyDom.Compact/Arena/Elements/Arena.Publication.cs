using System.Buffers;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed partial class Arena
{
    public CompactDocument Finalize(int root, CompactMetadataOptions options)
    {
        var preservesConstructionHandles = root == 0 && !_requiresRemap && _unattachedNodeCount == 0;
        int[]? order = null;
        int[]? remap = null;
        var orderCount = _columns.Count;
        if (!preservesConstructionHandles)
        {
            order = ArrayPool<int>.Shared.Rent(_columns.Count);
            orderCount = 0;
            AddPreOrder(root, order, ref orderCount);
            remap = ArrayPool<int>.Shared.Rent(_columns.Count);
            remap.AsSpan(0, _columns.Count).Fill(-1);
            for (var i = 0; i < orderCount; i++)
                remap[order[i]] = i;
        }

        var nodes = Allocate<CompactNode>(orderCount);
        var payloads = Allocate<CompactNodePayload>(_payloads?.Count ?? 0);
        var attributes = Allocate<CompactAttribute>(_attributes?.Count ?? 0);
        using var textBuilder = new PooledValueBuffer<char>(
            ValidateCapacity(_hints.InitialTextCapacity, nameof(CompactParserHints.InitialTextCapacity))
        );
        var parents = options.HasFlag(CompactMetadataOptions.ParentLinks) ? Allocate<int>(orderCount) : null;
        var sources = options.HasFlag(CompactMetadataOptions.SourceLocations)
            ? Allocate<CompactSourceLocation>(orderCount)
            : null;
        var subtreeEnds = Allocate<int>(orderCount);
        FillSubtreeEnds(subtreeEnds.AsSpan(0, orderCount), orderCount, preservesConstructionHandles, order, remap);
        var attributeIndex = 0;
        var payloadIndex = 0;

        for (var handle = 0; handle < orderCount; handle++)
        {
            var oldHandle = preservesConstructionHandles ? handle : order![handle];
            var first = FinalFirstChild(oldHandle);
            var firstChild =
                first < 0 ? -1
                : preservesConstructionHandles ? first
                : remap![first];
            var nodePayload = -1;
            var stateAttributeCount = AttributeCount(oldHandle);
            var stateValue = Value(oldHandle);
            if (stateAttributeCount != 0 || stateValue.Length != 0)
            {
                var firstAttribute = attributeIndex;
                for (var a = FirstAttribute(oldHandle); a >= 0; a = _attributes![a].Next)
                {
                    var value = CopyText(_attributes![a].Value, textBuilder);
                    attributes[attributeIndex++] = new CompactAttribute(
                        _attributes[a].NameId,
                        value.Start,
                        value.Length
                    );
                }
                var nodeValue = CopyText(stateValue, textBuilder);
                nodePayload = payloadIndex;
                payloads[payloadIndex++] = new CompactNodePayload(
                    firstAttribute,
                    nodeValue.Start,
                    nodeValue.Length,
                    checked((ushort)stateAttributeCount)
                );
            }

            nodes[handle] = new CompactNode(
                firstChild,
                subtreeEnds[handle],
                nodePayload,
                _columns.NameIds[oldHandle],
                _columns.Kinds[oldHandle],
                (byte)_columns.Flags[oldHandle]
            );
            if (parents is not null)
            {
                var parent = _columns.Parents[oldHandle];
                parents[handle] =
                    parent < 0 ? -1
                    : preservesConstructionHandles ? parent
                    : remap![parent];
            }
            if (sources is not null)
                sources[handle] = GetSource(_columns.SourceReferences?[oldHandle]);
        }

        var nameArray = CopyCustomNames(_names);
        var templateBoundaries = CreateTemplateBoundaries(orderCount, preservesConstructionHandles, order, remap);
        var (text, textLength) = textBuilder.Detach();
        var result = new CompactDocument(
            nodes,
            payloads,
            attributes,
            nameArray,
            text,
            parents,
            sources,
            templateBoundaries,
            orderCount,
            payloadIndex,
            attributeIndex,
            _names.CustomCount,
            textLength
        );
        if (order is not null)
            ArrayPool<int>.Shared.Return(order);
        if (remap is not null)
            ArrayPool<int>.Shared.Return(remap);
        ArrayPool<int>.Shared.Return(subtreeEnds);
        return result;
    }

    public bool CanFreeze(int root) => root == 0 && !_requiresRemap && _unattachedNodeCount == 0;

    public CompactDocument Freeze(int root, CompactMetadataOptions options, TextSource source)
    {
        if (!CanFreeze(root))
            throw new InvalidOperationException("This arena requires packed finalization.");

        var attributeCount = _attributes?.Count ?? 0;
        var nameArray = CopyCustomNames(_names);
        var templateBoundaries = CreateTemplateBoundaries(_columns.Count, true, null, null);
        var ownedText = OwnTextValues();
        FillSubtreeEnds(
            _columns.NextSiblings.AsSpan(0, _columns.Count),
            _columns.Count,
            preservesConstructionHandles: true,
            order: null,
            remap: null
        );
        _columns.ReleaseConstructionColumns(options.HasFlag(CompactMetadataOptions.ParentLinks));
        _nodes?.Dispose();
        _attributeWrappers?.Dispose();
        return new CompactDocument(
            this,
            source,
            nameArray,
            _columns.Count,
            _payloads?.Count ?? 0,
            attributeCount,
            _names.CustomCount,
            _textLength,
            options,
            templateBoundaries,
            ownedText
        );
    }

    internal ushort FrozenNameId(int handle) => _columns.NameIds[handle];

    internal ushort FrozenAttributeNameId(int attribute) => _attributes![attribute].NameId;

    internal ReadOnlySpan<ushort> NameIdColumn => _columns.NameIds.AsSpan(0, _columns.Count);

    internal int FrozenFirstChild(int handle) => FinalFirstChild(handle);

    internal int FrozenSubtreeEnd(int handle) => _columns.NextSiblings[handle];

    internal int FrozenPayloadIndex(int handle) => _columns.PayloadIndexes[handle];

    internal CompactNodeKind FrozenKind(int handle) => _columns.Kinds[handle];

    internal byte FrozenFlags(int handle) => (byte)_columns.Flags[handle];

    internal int FrozenParent(int handle) => _columns.Parents[handle];

    internal int FrozenFirstAttribute(int payload) => _payloads![payload].FirstAttribute;

    internal ushort FrozenAttributeCount(int payload) => _payloads![payload].AttributeCount;

    internal ReadOnlyMemory<char> FrozenPayloadValue(int payload) => _payloads![payload].Value.Memory;

    internal ReadOnlyMemory<char> FrozenAttributeValue(int attribute) => _attributes![attribute].Value.Memory;

    internal bool TryGetFrozenSourceLocation(int handle, out CompactSourceLocation source)
    {
        source = GetSource(_columns.SourceReferences?[handle]);
        return source.Index >= 0;
    }

    private static T[] Allocate<T>(int length) => ArrayPool<T>.Shared.Rent(length);
}
