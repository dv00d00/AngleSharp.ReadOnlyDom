using System.Buffers;
using AngleSharp.Dom;
using AngleSharp.ReadOnlyDom.Compact.Document;
using AngleSharp.ReadOnlyDom.Compact.Parsing;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed partial class Arena
{
    internal ReadOnlySpan<ushort> NameIdColumn => _columns.NameIds.AsSpan(0, _columns.Count);

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

    public bool CanFreeze(int root)
    {
        return root == 0 && !_requiresRemap && _unattachedNodeCount == 0;
    }

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
            true,
            null,
            null
        );
        _columns.ReleaseConstructionColumns(options.HasFlag(CompactMetadataOptions.ParentLinks));
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

    internal ushort FrozenNameId(int handle)
    {
        return _columns.NameIds[handle];
    }

    internal ushort FrozenAttributeNameId(int attribute)
    {
        return _attributes![attribute].NameId;
    }

    internal int FrozenFirstChild(int handle)
    {
        return FinalFirstChild(handle);
    }

    internal int FrozenSubtreeEnd(int handle)
    {
        return _columns.NextSiblings[handle];
    }

    internal int FrozenPayloadIndex(int handle)
    {
        return _columns.PayloadIndexes[handle];
    }

    internal CompactNodeKind FrozenKind(int handle)
    {
        return _columns.Kinds[handle];
    }

    internal byte FrozenFlags(int handle)
    {
        return (byte)_columns.Flags[handle];
    }

    internal int FrozenParent(int handle)
    {
        return _columns.Parents[handle];
    }

    internal int FrozenFirstAttribute(int payload)
    {
        return _payloads![payload].FirstAttribute;
    }

    internal ushort FrozenAttributeCount(int payload)
    {
        return _payloads![payload].AttributeCount;
    }

    internal ReadOnlyMemory<char> FrozenPayloadValue(int payload)
    {
        return _payloads![payload].Value.Memory;
    }

    internal ReadOnlyMemory<char> FrozenAttributeValue(int attribute)
    {
        return _attributes![attribute].Value.Memory;
    }

    internal bool TryGetFrozenSourceLocation(int handle, out CompactSourceLocation source)
    {
        source = GetSource(_columns.SourceReferences?[handle]);
        return source.Index >= 0;
    }

    private static T[] Allocate<T>(int length)
    {
        return ArrayPool<T>.Shared.Rent(length);
    }

    private void AddPreOrder(int handle, int[] order, ref int count)
    {
        order[count++] = handle;
        for (var child = FinalFirstChild(handle); child >= 0; child = _columns.NextSiblings[child])
            AddPreOrder(child, order, ref count);
    }

    private int FinalFirstChild(int handle)
    {
        return _columns.TemplateFirstChild(handle) is var template && template >= 0
            ? template
            : _columns.FirstChildren[handle];
    }

    private void FillSubtreeEnds(
        Span<int> destination,
        int outputCount,
        bool preservesConstructionHandles,
        int[]? order,
        int[]? remap
    )
    {
        var open = ArrayPool<int>.Shared.Rent(outputCount);
        var openCount = 0;
        try
        {
            for (var outputHandle = 0; outputHandle < outputCount; outputHandle++)
            {
                var constructionHandle = preservesConstructionHandles ? outputHandle : order![outputHandle];
                var constructionParent = _columns.Parents[constructionHandle];
                var outputParent =
                    constructionParent < 0 ? -1
                    : preservesConstructionHandles ? constructionParent
                    : remap![constructionParent];

                while (openCount > 0 && open[openCount - 1] != outputParent)
                    destination[open[--openCount]] = outputHandle;

                open[openCount++] = outputHandle;
            }

            while (openCount > 0)
                destination[open[--openCount]] = outputCount;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(open);
        }
    }

    private CompactTemplateBoundary[] CreateTemplateBoundaries(
        int outputCount,
        bool preservesConstructionHandles,
        int[]? order,
        int[]? remap
    )
    {
        List<CompactTemplateBoundary>? boundaries = null;
        for (var outputHandle = 0; outputHandle < outputCount; outputHandle++)
        {
            var constructionHandle = preservesConstructionHandles ? outputHandle : order![outputHandle];
            if (!IsHtmlTemplate(constructionHandle))
                continue;

            var constructionStart = _columns.TemplateFirstChild(constructionHandle);
            var contentStart =
                constructionStart < 0 ? -1
                : preservesConstructionHandles ? constructionStart
                : remap![constructionStart];
            var contentEnd = contentStart;
            if (contentStart >= 0)
            {
                contentEnd = outputHandle + 1;
                while (contentEnd < outputCount)
                {
                    var candidate = preservesConstructionHandles ? contentEnd : order![contentEnd];
                    if (!IsDescendantOf(candidate, constructionHandle))
                        break;
                    contentEnd++;
                }
            }

            (boundaries ??= []).Add(new CompactTemplateBoundary(outputHandle, contentStart, contentEnd));
        }

        return boundaries?.ToArray() ?? [];
    }

    private bool IsDescendantOf(int candidate, int ancestor)
    {
        for (var parent = _columns.Parents[candidate]; parent >= 0; parent = _columns.Parents[parent])
            if (parent == ancestor)
                return true;
        return false;
    }

    private bool IsHtmlTemplate(int handle)
    {
        return (_columns.Flags[handle] & NodeFlags.HtmlMember) != 0
               && _names.GetName(_columns.NameIds[handle]).Equals(TagNames.Template);
    }

    private static CompactSourceLocation GetSource(ISourceReference? source)
    {
        if (source is null)
            return new CompactSourceLocation(-1, 0, 0);
        var position = source.Position;
        return new CompactSourceLocation(position.Index, position.Line, position.Column);
    }

    private static string[] CopyCustomNames(NameTable nameTable)
    {
        if (nameTable.CustomCount == 0)
            return [];
        var names = Allocate<string>(nameTable.CustomCount);
        nameTable.CopyCustomNamesTo(names);
        return names;
    }
}