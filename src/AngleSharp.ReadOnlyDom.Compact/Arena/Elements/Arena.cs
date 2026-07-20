using System.Buffers;
using System.Runtime.InteropServices;
using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed class Arena : IDisposable
{
    private static ReadOnlySpan<char> WhiteSpace => " \t\r\n";
    private readonly PooledReferenceBuffer<ArenaNode> _nodes;
    private readonly MutableNodeColumns _columns;
    private readonly NameTable _names = new();
    private readonly CompactParserHints _hints;
    private readonly ICompactConstructionViewState? _constructionView;
    private PooledValueBuffer<MutableNodePayload>? _payloads;
    private PooledValueBuffer<MutableAttribute>? _attributes;
    private PooledReferenceBuffer<ArenaAttribute>? _attributeWrappers;
    private int _unattachedNodeCount;
    private int _textLength;
    private bool _requiresRemap;
    private readonly ushort _textNameId;

    public Arena(
        CompactParserHints hints,
        bool trackSourceReferences,
        ICompactConstructionViewState? constructionView = null
    )
    {
        _hints = hints;
        _constructionView = constructionView;
        _nodes = new PooledReferenceBuffer<ArenaNode>(
            ValidateCapacity(hints.InitialNodeCapacity, nameof(CompactParserHints.InitialNodeCapacity))
        );
        _columns = new MutableNodeColumns(hints.InitialNodeCapacity, trackSourceReferences);
        _textNameId = _names.GetId("#text");
    }

    public ArenaDocument CreateDocument(TextSource source, CompactMetadataOptions options, CompactDocumentLayout layout)
    {
        var document = new ArenaDocument(
            this,
            AddState("#document", NodeFlags.None, CompactNodeKind.Document),
            source,
            options,
            layout
        );
        _unattachedNodeCount--;
        _nodes.Add(document);
        return document;
    }

    public ArenaElement CreateElement(
        StringOrMemory name,
        StringOrMemory prefix,
        StringOrMemory namespaceUri,
        NodeFlags flags,
        ElementMarker marker = ElementMarker.None
    )
    {
        var qualifiedName = prefix.IsNullOrEmpty ? name : $"{prefix}:{name}";
        var handle = AddState(qualifiedName, flags, CompactNodeKind.Element);
        ArenaElement node = marker switch
        {
            ElementMarker.Template => new ArenaTemplateElement(this, handle),
            ElementMarker.Script => new ArenaScriptElement(this, handle),
            ElementMarker.Meta => new ArenaMetaElement(this, handle),
            ElementMarker.Form => new ArenaFormElement(this, handle),
            ElementMarker.Frame => new ArenaFrameElement(this, handle),
            ElementMarker.Math => new ArenaMathElement(this, handle),
            ElementMarker.Svg => new ArenaSvgElement(this, handle),
            _ => new ArenaElement(this, handle),
        };
        _nodes.Add(node);
        return node;
    }

    public ArenaNode CreateLeaf(StringOrMemory name, StringOrMemory value, CompactNodeKind kind)
    {
        var handle = AddState(name, NodeFlags.None, kind);
        SetValue(handle, value);
        var node = new ArenaNode(this, handle);
        _nodes.Add(node);
        return node;
    }

    private int AddLeaf(StringOrMemory name, StringOrMemory value, CompactNodeKind kind)
    {
        var handle = AddState(name, NodeFlags.None, kind);
        SetValue(handle, value);
        _nodes.AddEmpty();
        return handle;
    }

    private int AddTextLeaf(StringOrMemory value)
    {
        _unattachedNodeCount++;
        _constructionView?.NodeMaterialized();
        var handle = _columns.Add(_textNameId, NodeFlags.None, CompactNodeKind.Text);
        SetValue(handle, value);
        _nodes.AddEmpty();
        return handle;
    }

    public ArenaNode Node(int handle)
    {
        var node = _nodes[handle];
        if (node is null)
        {
            node = new ArenaNode(this, handle);
            _nodes[handle] = node;
        }
        return node;
    }

    public StringOrMemory Name(int handle) => _names.GetName(_columns.NameIds[handle]);

    public StringOrMemory LocalName(int handle)
    {
        // Generated known names never contain ':'; only custom (prefixed) names need the scan.
        var id = _columns.NameIds[handle];
        if (id < GeneratedTagMetadata.KnownNameCount)
            return GeneratedTagMetadata.GetKnownName(id);
        var name = _names.GetName(id);
        var separator = name.Memory.Span.IndexOf(':');
        return separator < 0 ? name : (StringOrMemory)name.Memory.Slice(separator + 1);
    }

    public StringOrMemory Prefix(int handle)
    {
        var id = _columns.NameIds[handle];
        if (id < GeneratedTagMetadata.KnownNameCount)
            return default;
        var name = _names.GetName(id);
        var separator = name.Memory.Span.IndexOf(':');
        return separator < 0 ? default : (StringOrMemory)name.Memory.Slice(0, separator);
    }

    public StringOrMemory NamespaceUri(int handle) =>
        (_columns.Flags[handle] & NodeFlags.SvgMember) != 0 ? NamespaceNames.SvgUri
        : (_columns.Flags[handle] & NodeFlags.MathMember) != 0 ? NamespaceNames.MathMlUri
        : NamespaceNames.HtmlUri;

    public StringOrMemory Value(int handle)
    {
        var payload = _columns.PayloadIndexes[handle];
        return payload < 0 ? default : _payloads![payload].Value;
    }

    public NodeFlags Flags(int handle) => _columns.Flags[handle];

    public CompactNodeKind Kind(int handle) => _columns.Kinds[handle];

    public int Parent(int handle) => _columns.Parents[handle];

    internal int FirstChild(int handle) => _columns.FirstChildren[handle];

    internal int NextSibling(int handle) => _columns.NextSiblings[handle];

    public int ChildCount(int handle) => _columns.ChildCounts[handle];

    public int ChildAt(int handle, int index)
    {
        if ((uint)index >= (uint)_columns.ChildCounts[handle])
            throw new ArgumentOutOfRangeException(nameof(index));
        var child = _columns.FirstChildren[handle];
        while (index-- > 0)
            child = _columns.NextSiblings[child];
        return child;
    }

    public int AttributeCount(int handle)
    {
        var payload = _columns.PayloadIndexes[handle];
        return payload < 0 ? 0 : _payloads![payload].AttributeCount;
    }

    public ArenaAttribute? GetAttribute(int handle, StringOrMemory name)
    {
        if (!_names.TryGetId(name, out var nameId))
            return null;
        var attribute = FirstAttribute(handle);
        while (attribute >= 0)
        {
            if (_attributes![attribute].NameId == nameId)
                return _attributeWrappers![attribute];
            attribute = _attributes[attribute].Next;
        }
        return null;
    }

    public IEnumerable<ArenaAttribute> Attributes(int handle)
    {
        for (var attribute = FirstAttribute(handle); attribute >= 0; attribute = _attributes![attribute].Next)
            yield return _attributeWrappers![attribute]!;
    }

    public StringOrMemory AttributeName(int handle) => _names.GetName(_attributes![handle].NameId);

    public StringOrMemory AttributeValue(int handle) => _attributes![handle].Value;

    internal int FirstAttributeHandle(int handle) => FirstAttribute(handle);

    internal int NextAttribute(int attribute) => _attributes![attribute].Next;

    internal bool AttributesSame(int left, int right)
    {
        if (AttributeCount(left) != AttributeCount(right))
            return false;
        var attributes = _attributes;
        if (attributes is null)
            return true;
        for (var attribute = FirstAttribute(left); attribute >= 0; attribute = attributes[attribute].Next)
        {
            var candidate = FirstAttribute(right);
            while (
                candidate >= 0
                && (
                    attributes[candidate].NameId != attributes[attribute].NameId
                    || attributes[candidate].Value != attributes[attribute].Value
                )
            )
            {
                candidate = attributes[candidate].Next;
            }
            if (candidate < 0)
                return false;
        }
        return true;
    }

    public void SetAttributeValue(int handle, StringOrMemory value)
    {
        ref var attribute = ref _attributes![handle];
        _textLength = checked(_textLength - attribute.Value.Length + value.Length);
        attribute.Value = value;
    }

    public ISourceReference? SourceReference(int handle) => _columns.SourceReferences?[handle];

    public void SetSourceReference(int handle, ISourceReference? value)
    {
        if (_columns.SourceReferences is not null)
            _columns.SourceReferences[handle] = value;
    }

    public void AddChild(int parent, int child, int? index = null)
    {
        if (_columns.Parents[child] >= 0 || child != _nodes.Count - 1)
            _requiresRemap = true;
        Detach(child);
        _unattachedNodeCount--;
        if (index is null || index.Value == _columns.ChildCounts[parent])
        {
            AppendChild(parent, child);
            return;
        }
        if ((uint)index.Value > (uint)_columns.ChildCounts[parent])
            throw new ArgumentOutOfRangeException(nameof(index));
        InsertBefore(parent, child, ChildAt(parent, index.Value));
    }

    public void AddText(int parent, StringOrMemory text, bool emitWhiteSpaceOnly, int? index = null)
    {
        if (
            _constructionView is null
            && !emitWhiteSpaceOnly
            && text.Memory.Span.Trim(WhiteSpace).Length == 0
            && !ShouldRetainWhitespaceAt(parent, index)
        )
            return;
        var retained = _constructionView?.SelectTextValue(text) ?? text;
        AddChild(parent, AddTextLeaf(retained), index);
    }

    private bool ShouldRetainWhitespaceAt(int parent, int? index)
    {
        if (_columns.Kinds[parent] == CompactNodeKind.Element && (_columns.Flags[parent] & NodeFlags.Special) == 0)
        {
            return true;
        }

        var previous = index is > 0 ? ChildAt(parent, index.Value - 1) : _columns.LastChildren[parent];
        while (previous >= 0)
        {
            var isPhrasing = IsPhrasingContent(previous);
            if (isPhrasing.HasValue)
                return isPhrasing.Value;
            previous = _columns.PreviousSiblings[previous];
        }

        var next = index is { } position && position < _columns.ChildCounts[parent] ? ChildAt(parent, position) : -1;
        while (next >= 0)
        {
            var isPhrasing = IsPhrasingContent(next);
            if (isPhrasing.HasValue)
                return isPhrasing.Value;
            next = _columns.NextSiblings[next];
        }

        return false;
    }

    private bool? IsPhrasingContent(int handle) =>
        _columns.Kinds[handle] switch
        {
            CompactNodeKind.Text => true,
            CompactNodeKind.Element => (_columns.Flags[handle] & NodeFlags.Special) == 0,
            CompactNodeKind.Comment or CompactNodeKind.ProcessingInstruction => null,
            _ => false,
        };

    public void AddComment(int parent, ref StructHtmlToken token)
    {
        if (token.IsEmpty)
            return;
        if (token.IsProcessingInstruction)
        {
            var data = token.Data.Memory;
            var separator = data.Span.IndexOf(' ');
            var target = separator <= 0 ? token.Data : (StringOrMemory)data.Slice(0, separator);
            var value = separator <= 0 ? StringOrMemory.Empty : (StringOrMemory)data.Slice(separator);
            AddChild(parent, AddLeaf(target, value, CompactNodeKind.ProcessingInstruction));
        }
        else
        {
            AddChild(parent, AddLeaf("#comment", token.Data, CompactNodeKind.Comment));
        }
    }

    public CompactDocument Finalize(int root, CompactMetadataOptions options)
    {
        var preservesConstructionHandles = root == 0 && !_requiresRemap && _unattachedNodeCount == 0;
        int[]? order = null;
        int[]? remap = null;
        var orderCount = _nodes.Count;
        if (!preservesConstructionHandles)
        {
            order = ArrayPool<int>.Shared.Rent(_nodes.Count);
            orderCount = 0;
            AddPreOrder(root, order, ref orderCount);
            remap = ArrayPool<int>.Shared.Rent(_nodes.Count);
            remap.AsSpan(0, _nodes.Count).Fill(-1);
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
        _columns.ReleaseConstructionColumns();
        _nodes.Dispose();
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

    private int AddState(StringOrMemory name, NodeFlags flags, CompactNodeKind kind)
    {
        _unattachedNodeCount++;
        _constructionView?.NodeMaterialized();
        return _columns.Add(_names.GetId(name), flags, kind);
    }

    private void AddPreOrder(int handle, int[] order, ref int count)
    {
        order[count++] = handle;
        for (var child = FinalFirstChild(handle); child >= 0; child = _columns.NextSiblings[child])
            AddPreOrder(child, order, ref count);
    }

    private int FinalFirstChild(int handle) =>
        _columns.TemplateFirstChild(handle) is var template && template >= 0
            ? template
            : _columns.FirstChildren[handle];

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
            if (_nodes[constructionHandle] is not ArenaTemplateElement)
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

    private char[] OwnTextValues()
    {
        var payloadCount = _payloads?.Count ?? 0;
        var attributeCount = _attributes?.Count ?? 0;

        // Values are slices of the tokenizer's append-only char buffer (one array per parse) or
        // interned strings. Copying each backing array's used range once and rebasing the slices
        // with offset arithmetic is much cheaper than a per-value copy; strings need no copy at all.
        var regions = new ValueRegion[4];
        var regionCount = 0;
        var bulk = true;
        for (var payload = 0; payload < payloadCount && bulk; payload++)
            bulk = TrackValueRegion(_payloads![payload].Value, regions, ref regionCount);
        for (var attribute = 0; attribute < attributeCount && bulk; attribute++)
            bulk = TrackValueRegion(_attributes![attribute].Value, regions, ref regionCount);

        if (bulk)
        {
            var total = 0;
            for (var region = 0; region < regionCount; region++)
                total += regions[region].End - regions[region].Start;
            // Gaps between retained values (filtered attributes, dropped whitespace) inflate the
            // range; fall back to the dense copy when the overhead outgrows the retained text.
            if (total <= Math.Max(_textLength * 2, 4096))
            {
                var text = Allocate<char>(Math.Max(total, 1));
                var position = 0;
                for (var region = 0; region < regionCount; region++)
                {
                    ref var current = ref regions[region];
                    current.DestinationStart = position;
                    current.Array.AsSpan(current.Start, current.End - current.Start).CopyTo(text.AsSpan(position));
                    position += current.End - current.Start;
                }
                for (var payload = 0; payload < payloadCount; payload++)
                    RebaseValue(ref _payloads![payload].Value, regions, regionCount, text);
                for (var attribute = 0; attribute < attributeCount; attribute++)
                    RebaseValue(ref _attributes![attribute].Value, regions, regionCount, text);
                return text;
            }
        }

        var dense = Allocate<char>(_textLength);
        var densePosition = 0;
        for (var payload = 0; payload < payloadCount; payload++)
            OwnTextValue(ref _payloads![payload].Value, dense, ref densePosition);
        for (var attribute = 0; attribute < attributeCount; attribute++)
            OwnTextValue(ref _attributes![attribute].Value, dense, ref densePosition);
        return dense;
    }

    private struct ValueRegion
    {
        public char[] Array;
        public int Start;
        public int End;
        public int DestinationStart;
    }

    /// <summary>Extends the backing-array regions with one value; false forces the dense fallback.</summary>
    private static bool TrackValueRegion(in StringOrMemory value, ValueRegion[] regions, ref int regionCount)
    {
        if (value.Length == 0)
            return true;
        if (!MemoryMarshal.TryGetArray(value.Memory, out var segment))
            return MemoryMarshal.TryGetString(value.Memory, out _, out _, out _);
        for (var region = 0; region < regionCount; region++)
        {
            ref var current = ref regions[region];
            if (!ReferenceEquals(current.Array, segment.Array))
                continue;
            current.Start = Math.Min(current.Start, segment.Offset);
            current.End = Math.Max(current.End, segment.Offset + segment.Count);
            return true;
        }
        if (regionCount == regions.Length)
            return false;
        regions[regionCount++] = new ValueRegion
        {
            Array = segment.Array!,
            Start = segment.Offset,
            End = segment.Offset + segment.Count,
        };
        return true;
    }

    private static void RebaseValue(ref StringOrMemory value, ValueRegion[] regions, int regionCount, char[] text)
    {
        if (value.Length == 0 || !MemoryMarshal.TryGetArray(value.Memory, out var segment))
            return;
        for (var region = 0; region < regionCount; region++)
        {
            ref var current = ref regions[region];
            if (!ReferenceEquals(current.Array, segment.Array))
                continue;
            value = new StringOrMemory(
                text.AsMemory(current.DestinationStart + segment.Offset - current.Start, segment.Count)
            );
            return;
        }
    }

    private static void OwnTextValue(ref StringOrMemory value, char[] destination, ref int position)
    {
        if (value.Length == 0)
            return;
        value.Memory.Span.CopyTo(destination.AsSpan(position));
        value = new StringOrMemory(destination.AsMemory(position, value.Length));
        position += value.Length;
    }

    private static (int Start, int Length) CopyText(StringOrMemory value, PooledValueBuffer<char> destination)
    {
        if (value.Length == 0)
            return (-1, 0);
        var start = destination.Count;
        destination.AddRange(value.Memory.Span);
        return (start, value.Length);
    }

    private static CompactSourceLocation GetSource(ISourceReference? source)
    {
        if (source is null)
            return new CompactSourceLocation(-1, 0, 0);
        var position = source.Position;
        return new CompactSourceLocation(position.Index, position.Line, position.Column);
    }

    public void Dispose()
    {
        _nodes.Dispose();
        _attributeWrappers?.Dispose();
        _attributes?.Dispose();
        _payloads?.Dispose();
        _columns.Dispose();
    }

    public void RemoveFromParent(int child)
    {
        if (_columns.Parents[child] >= 0)
            _requiresRemap = true;
        Detach(child);
    }

    public void RemoveChild(int parent, int child)
    {
        if (_columns.Parents[child] == parent)
        {
            _requiresRemap = true;
            Detach(child);
        }
    }

    public void ClearChildren(int parent)
    {
        if (_columns.ChildCounts[parent] != 0)
            _requiresRemap = true;
        var child = _columns.FirstChildren[parent];
        while (child >= 0)
        {
            var next = _columns.NextSiblings[child];
            _columns.Parents[child] = -1;
            _columns.PreviousSiblings[child] = -1;
            _columns.NextSiblings[child] = -1;
            _unattachedNodeCount++;
            child = next;
        }
        _columns.FirstChildren[parent] = -1;
        _columns.LastChildren[parent] = -1;
        _columns.ChildCounts[parent] = 0;
    }

    public void PopulateTemplate(int handle)
    {
        _columns.SetTemplateFirstChild(handle, _columns.FirstChildren[handle]);
        _columns.FirstChildren[handle] = -1;
        _columns.LastChildren[handle] = -1;
        _columns.ChildCounts[handle] = 0;
    }

    public void SetOwnAttribute(int handle, StringOrMemory name, StringOrMemory value)
    {
        // Resolve the name once; the duplicate check and the append share the ID.
        var nameId = _names.GetId(name);
        for (var existing = FirstAttribute(handle); existing >= 0; existing = _attributes![existing].Next)
        {
            if (_attributes![existing].NameId == nameId)
            {
                SetAttributeValue(existing, value);
                return;
            }
        }

        _attributes ??= new PooledValueBuffer<MutableAttribute>(
            ValidateCapacity(_hints.InitialAttributeCapacity, nameof(CompactParserHints.InitialAttributeCapacity))
        );
        _attributeWrappers ??= new PooledReferenceBuffer<ArenaAttribute>(
            ValidateCapacity(_hints.InitialAttributeCapacity, nameof(CompactParserHints.InitialAttributeCapacity))
        );
        var payloadIndex = EnsurePayload(handle);
        ref var payload = ref _payloads![payloadIndex];
        var attributeHandle = _attributes.Add(new MutableAttribute(nameId, value));
        _textLength = checked(_textLength + value.Length);
        var wrapper = new ArenaAttribute(this, attributeHandle);
        _attributeWrappers.Add(wrapper);
        if (payload.FirstAttribute < 0)
            payload.FirstAttribute = attributeHandle;
        else
            _attributes[payload.LastAttribute].Next = attributeHandle;
        payload.LastAttribute = attributeHandle;
        payload.AttributeCount++;
        _constructionView?.AttributeRetained(value);
    }

    public void CompleteAttributes(int handle) => _constructionView?.CompleteAttributes(this, handle);

    public CompactStreamingExtractionResult CreateStreamingExtractionResult(int root, int inputBytesConsumed) =>
        (_constructionView as CompactStreamingExtractionState)?.CreateResult(this, root, inputBytesConsumed)
        ?? throw new InvalidOperationException("The arena was not configured for streaming extraction.");

    public CompactAggregateResult CreateAggregateResult(int root, int inputBytesConsumed) =>
        (_constructionView as CompactAggregateExecutionState)?.CreateResult(this, root, inputBytesConsumed)
        ?? throw new InvalidOperationException("The arena was not configured for aggregate extraction.");

    public void SetTokensProcessed(int count) => _constructionView?.SetTokensProcessed(count);

    public void CopyAttributes(int source, int destination)
    {
        foreach (var attribute in Attributes(source))
            SetOwnAttribute(destination, attribute.Name, attribute.Value);
    }

    private void SetValue(int handle, StringOrMemory value)
    {
        if (value.Length != 0)
        {
            var payload = EnsurePayload(handle);
            _textLength = checked(_textLength - _payloads![payload].Value.Length + value.Length);
            _payloads![payload].Value = value;
        }
    }

    private static string[] CopyCustomNames(NameTable nameTable)
    {
        if (nameTable.CustomCount == 0)
            return [];
        var names = Allocate<string>(nameTable.CustomCount);
        nameTable.CopyCustomNamesTo(names);
        return names;
    }

    private int FirstAttribute(int handle)
    {
        var payload = _columns.PayloadIndexes[handle];
        return payload < 0 ? -1 : _payloads![payload].FirstAttribute;
    }

    private int EnsurePayload(int handle)
    {
        var payload = _columns.PayloadIndexes[handle];
        if (payload >= 0)
            return payload;
        _payloads ??= new PooledValueBuffer<MutableNodePayload>(
            ValidateCapacity(_hints.InitialPayloadCapacity, nameof(CompactParserHints.InitialPayloadCapacity))
        );
        payload = _payloads.Add(new MutableNodePayload());
        _columns.PayloadIndexes[handle] = payload;
        return payload;
    }

    private static int ValidateCapacity(int capacity, string name) =>
        capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(name, "Capacity hints must be positive.");

    private void AppendChild(int parent, int child)
    {
        var previous = _columns.LastChildren[parent];
        _columns.Parents[child] = parent;
        _columns.PreviousSiblings[child] = previous;
        _columns.NextSiblings[child] = -1;
        if (previous >= 0)
            _columns.NextSiblings[previous] = child;
        else
            _columns.FirstChildren[parent] = child;
        _columns.LastChildren[parent] = child;
        _columns.ChildCounts[parent]++;
    }

    private void InsertBefore(int parent, int child, int next)
    {
        _requiresRemap = true;
        var previous = _columns.PreviousSiblings[next];
        _columns.Parents[child] = parent;
        _columns.PreviousSiblings[child] = previous;
        _columns.NextSiblings[child] = next;
        _columns.PreviousSiblings[next] = child;
        if (previous >= 0)
            _columns.NextSiblings[previous] = child;
        else
            _columns.FirstChildren[parent] = child;
        _columns.ChildCounts[parent]++;
    }

    private void Detach(int child)
    {
        var parent = _columns.Parents[child];
        if (parent < 0)
            return;
        var previous = _columns.PreviousSiblings[child];
        var next = _columns.NextSiblings[child];
        if (previous >= 0)
            _columns.NextSiblings[previous] = next;
        else
            _columns.FirstChildren[parent] = next;
        if (next >= 0)
            _columns.PreviousSiblings[next] = previous;
        else
            _columns.LastChildren[parent] = previous;
        _columns.Parents[child] = -1;
        _columns.PreviousSiblings[child] = -1;
        _columns.NextSiblings[child] = -1;
        _columns.ChildCounts[parent]--;
        _unattachedNodeCount++;
    }
}
