using System.Buffers;
using System.Collections;
using System.Runtime.CompilerServices;
using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.CompactPrototype;

public sealed class CompactParserHints
{
    public int InitialNodeCapacity { get; init; } = 64;
    public int InitialPayloadCapacity { get; init; } = 32;
    public int InitialAttributeCapacity { get; init; } = 16;
    public int InitialTextCapacity { get; init; } = 256;
}

public delegate bool CompactAttributeFilter(ref StructHtmlToken token, ReadOnlyMemory<char> attributeName);

public static class DirectCompactParser
{
    public static HotCompactDocument Parse(
        string html,
        CompactMetadataOptions options = CompactMetadataOptions.None,
        CompactBufferOwnership ownership = CompactBufferOwnership.Owned,
        CompactParserHints? hints = null,
        CompactAttributeFilter? attributeFilter = null,
        HtmlParserOptions? parserOptions = null,
        TokenizerMiddleware? middleware = null
    ) =>
        Parse(
            new TextSource(new StringTextSource(html)),
            options,
            ownership,
            hints,
            attributeFilter,
            parserOptions,
            middleware
        );

    public static HotCompactDocument Parse(
        ReadOnlyMemory<char> html,
        CompactMetadataOptions options = CompactMetadataOptions.None,
        CompactBufferOwnership ownership = CompactBufferOwnership.Owned,
        CompactParserHints? hints = null,
        CompactAttributeFilter? attributeFilter = null,
        HtmlParserOptions? parserOptions = null,
        TokenizerMiddleware? middleware = null
    ) =>
        Parse(
            new TextSource(new ReadOnlyMemoryTextSource(html)),
            options,
            ownership,
            hints,
            attributeFilter,
            parserOptions,
            middleware
        );

    public static HotCompactDocument Parse(
        char[] html,
        int length,
        CompactMetadataOptions options = CompactMetadataOptions.None,
        CompactBufferOwnership ownership = CompactBufferOwnership.Owned,
        CompactParserHints? hints = null,
        CompactAttributeFilter? attributeFilter = null,
        HtmlParserOptions? parserOptions = null,
        TokenizerMiddleware? middleware = null
    ) =>
        Parse(
            new TextSource(new CharArrayTextSource(html, length)),
            options,
            ownership,
            hints,
            attributeFilter,
            parserOptions,
            middleware
        );

    private static HotCompactDocument Parse(
        TextSource source,
        CompactMetadataOptions options,
        CompactBufferOwnership ownership,
        CompactParserHints? hints,
        CompactAttributeFilter? attributeFilter,
        HtmlParserOptions? parserOptions,
        TokenizerMiddleware? middleware
    )
    {
        hints ??= new CompactParserHints();
        var effectiveParserOptions = parserOptions ?? CreateParserOptions(options);
        ApplyAttributeFilter(ref effectiveParserOptions, attributeFilter);
        var configuration = Configuration.Default.With(_ => new ArenaConstructionFactory(
            ownership == CompactBufferOwnership.Pooled,
            hints,
            effectiveParserOptions.IsKeepingSourceReferences
        ));
        var context = BrowsingContext.New(configuration);
        var parser = new HtmlParser(effectiveParserOptions, context);
        var document = parser.ParseDocument<ArenaDocument, ArenaElement>(source, middleware);
        try
        {
            return document.Arena.Finalize(document.NodeHandle, options, ownership);
        }
        finally
        {
            document.Dispose();
        }
    }

    private static HtmlParserOptions CreateParserOptions(CompactMetadataOptions options)
    {
        var parserOptions = new HtmlParserOptions
        {
            SkipComments = true,
            SkipProcessingInstructions = true,
            IsKeepingSourceReferences = options.HasFlag(CompactMetadataOptions.SourceLocations),
        };
        return parserOptions;
    }

    private static void ApplyAttributeFilter(
        ref HtmlParserOptions parserOptions,
        CompactAttributeFilter? attributeFilter
    )
    {
        if (attributeFilter is not null)
            parserOptions.ShouldEmitAttribute = (ref StructHtmlToken token, ReadOnlyMemory<char> name) =>
                attributeFilter(ref token, name);
    }
}

public sealed class DirectCompactParserSession
{
    private readonly HtmlParser _parser;
    private readonly CompactMetadataOptions _options;
    private readonly CompactBufferOwnership _ownership;

    public DirectCompactParserSession(
        CompactMetadataOptions options = CompactMetadataOptions.None,
        CompactBufferOwnership ownership = CompactBufferOwnership.Owned,
        CompactParserHints? hints = null,
        CompactAttributeFilter? attributeFilter = null,
        HtmlParserOptions? parserOptions = null
    )
    {
        _options = options;
        _ownership = ownership;
        hints ??= new CompactParserHints();
        var effectiveParserOptions =
            parserOptions
            ?? new HtmlParserOptions
            {
                SkipComments = true,
                SkipProcessingInstructions = true,
                IsKeepingSourceReferences = options.HasFlag(CompactMetadataOptions.SourceLocations),
            };
        if (attributeFilter is not null)
            effectiveParserOptions.ShouldEmitAttribute = (ref StructHtmlToken token, ReadOnlyMemory<char> name) =>
                attributeFilter(ref token, name);
        var configuration = Configuration.Default.With(_ => new ArenaConstructionFactory(
            ownership == CompactBufferOwnership.Pooled,
            hints,
            effectiveParserOptions.IsKeepingSourceReferences
        ));
        var context = BrowsingContext.New(configuration);
        _parser = new HtmlParser(effectiveParserOptions, context);
    }

    public HotCompactDocument Parse(string html, TokenizerMiddleware? middleware = null) =>
        Parse(new TextSource(new StringTextSource(html)), middleware);

    public HotCompactDocument Parse(ReadOnlyMemory<char> html, TokenizerMiddleware? middleware = null) =>
        Parse(new TextSource(new ReadOnlyMemoryTextSource(html)), middleware);

    public HotCompactDocument Parse(char[] html, int length, TokenizerMiddleware? middleware = null) =>
        Parse(new TextSource(new CharArrayTextSource(html, length)), middleware);

    private HotCompactDocument Parse(TextSource source, TokenizerMiddleware? middleware)
    {
        var document = _parser.ParseDocument<ArenaDocument, ArenaElement>(source, middleware);
        try
        {
            return document.Arena.Finalize(document.NodeHandle, _options, _ownership);
        }
        finally
        {
            document.Dispose();
        }
    }
}

internal sealed class Arena : IDisposable
{
    private static ReadOnlySpan<char> WhiteSpace => " \t\r\n";
    private readonly IReferenceBuffer<ArenaNode> _nodes;
    private readonly MutableNodeColumns _columns;
    private readonly bool _pooled;
    private readonly CompactParserHints _hints;
    private PooledValueBuffer<MutableNodePayload>? _payloads;
    private PooledValueBuffer<MutableAttribute>? _attributes;
    private IReferenceBuffer<ArenaAttribute>? _attributeWrappers;

    public Arena(bool pooled, CompactParserHints hints, bool trackSourceReferences)
    {
        _pooled = pooled;
        _hints = hints;
        _nodes = pooled ? new PooledReferenceBuffer<ArenaNode>() : new ListReferenceBuffer<ArenaNode>();
        _columns = new MutableNodeColumns(pooled, hints.InitialNodeCapacity, trackSourceReferences);
    }

    public ArenaDocument CreateDocument(TextSource source)
    {
        var document = new ArenaDocument(
            this,
            AddState("#document", "#document", default, default, NodeFlags.None, CompactNodeKind.Document),
            source
        );
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
        var handle = AddState(qualifiedName, name, prefix, namespaceUri, flags, CompactNodeKind.Element);
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
        var handle = AddState(name, name, default, default, NodeFlags.None, kind);
        SetValue(handle, value);
        var node = new ArenaNode(this, handle);
        _nodes.Add(node);
        return node;
    }

    public ArenaNode Node(int handle) => _nodes[handle];

    public StringOrMemory Name(int handle) => _columns.Names[handle];

    public StringOrMemory LocalName(int handle)
    {
        var name = _columns.Names[handle];
        var separator = name.Memory.Span.IndexOf(':');
        return separator < 0 ? name : (StringOrMemory)name.Memory.Slice(separator + 1);
    }

    public StringOrMemory Prefix(int handle)
    {
        var name = _columns.Names[handle];
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
        var attribute = FirstAttribute(handle);
        while (attribute >= 0)
        {
            if (_attributes![attribute].Name == name)
                return _attributeWrappers![attribute];
            attribute = _attributes[attribute].Next;
        }
        return null;
    }

    public IEnumerable<ArenaAttribute> Attributes(int handle)
    {
        for (var attribute = FirstAttribute(handle); attribute >= 0; attribute = _attributes![attribute].Next)
            yield return _attributeWrappers![attribute];
    }

    public StringOrMemory AttributeName(int handle) => _attributes![handle].Name;

    public StringOrMemory AttributeValue(int handle) => _attributes![handle].Value;

    public void SetAttributeValue(int handle, StringOrMemory value) => _attributes![handle].Value = value;

    public ISourceReference? SourceReference(int handle) => _columns.SourceReferences?[handle];

    public void SetSourceReference(int handle, ISourceReference? value)
    {
        if (_columns.SourceReferences is not null)
            _columns.SourceReferences[handle] = value;
    }

    public void AddChild(int parent, int child, int? index = null)
    {
        Detach(child);
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
        if (!emitWhiteSpaceOnly && text.Memory.Span.Trim(WhiteSpace).Length == 0)
            return;
        var node = CreateLeaf("#text", text, CompactNodeKind.Text);
        AddChild(parent, node.NodeHandle, index);
    }

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
            AddChild(parent, CreateLeaf(target, value, CompactNodeKind.ProcessingInstruction).NodeHandle);
        }
        else
        {
            AddChild(parent, CreateLeaf("#comment", token.Data, CompactNodeKind.Comment).NodeHandle);
        }
    }

    public HotCompactDocument Finalize(int root, CompactMetadataOptions options, CompactBufferOwnership ownership)
    {
        var order = ArrayPool<int>.Shared.Rent(_nodes.Count);
        var orderCount = 0;
        AddPreOrder(root, order, ref orderCount);
        var remap = ArrayPool<int>.Shared.Rent(_nodes.Count);
        remap.AsSpan(0, _nodes.Count).Fill(-1);
        for (var i = 0; i < orderCount; i++)
            remap[order[i]] = i;

        var pooled = ownership == CompactBufferOwnership.Pooled;
        var nodes = Allocate<HotCompactNode>(orderCount, pooled);
        var payloads = Allocate<ColdNodePayload>(_payloads?.Count ?? 0, pooled);
        var attributes = Allocate<CompactAttribute>(_attributes?.Count ?? 0, pooled);
        using var textBuilder = new PooledValueBuffer<char>(
            pooled,
            ValidateCapacity(_hints.InitialTextCapacity, nameof(CompactParserHints.InitialTextCapacity))
        );
        var parents = options.HasFlag(CompactMetadataOptions.ParentLinks) ? Allocate<int>(orderCount, pooled) : null;
        var sources = options.HasFlag(CompactMetadataOptions.SourceLocations)
            ? Allocate<CompactSourceLocation>(orderCount, pooled)
            : null;
        var names = new NameTable();
        var attributeIndex = 0;
        var payloadIndex = 0;

        for (var handle = 0; handle < orderCount; handle++)
        {
            var oldHandle = order[handle];
            var first = FinalFirstChild(oldHandle);
            var firstChild = first < 0 ? -1 : remap[first];
            var sibling = _columns.NextSiblings[oldHandle];
            var nextSibling = sibling < 0 ? -1 : remap[sibling];

            var nodePayload = -1;
            var stateAttributeCount = AttributeCount(oldHandle);
            var stateValue = Value(oldHandle);
            if (stateAttributeCount != 0 || stateValue.Length != 0)
            {
                var firstAttribute = attributeIndex;
                foreach (var attribute in Attributes(oldHandle))
                {
                    var value = CopyText(attribute.Value, textBuilder);
                    attributes[attributeIndex++] = new CompactAttribute(
                        names.GetId(attribute.Name),
                        value.Start,
                        value.Length
                    );
                }
                var nodeValue = CopyText(stateValue, textBuilder);
                nodePayload = payloadIndex;
                payloads[payloadIndex++] = new ColdNodePayload(
                    firstAttribute,
                    nodeValue.Start,
                    nodeValue.Length,
                    checked((ushort)stateAttributeCount)
                );
            }

            nodes[handle] = new HotCompactNode(
                firstChild,
                nextSibling,
                nodePayload,
                names.GetId(_columns.Names[oldHandle]),
                _columns.Kinds[oldHandle],
                (byte)_columns.Flags[oldHandle]
            );
            if (parents is not null)
            {
                var parent = _columns.Parents[oldHandle];
                parents[handle] = parent < 0 ? -1 : remap[parent];
            }
            if (sources is not null)
                sources[handle] = GetSource(_columns.SourceReferences?[oldHandle]);
        }

        var nameArray = Allocate<string>(names.Count, pooled);
        names.CopyTo(nameArray);
        var (text, textLength) = textBuilder.Detach();
        var result = new HotCompactDocument(
            nodes,
            payloads,
            attributes,
            nameArray,
            text,
            parents,
            sources,
            orderCount,
            payloadIndex,
            attributeIndex,
            names.Count,
            textLength,
            pooled
        );
        ArrayPool<int>.Shared.Return(order);
        ArrayPool<int>.Shared.Return(remap);
        return result;
    }

    private static T[] Allocate<T>(int length, bool pooled) =>
        pooled ? ArrayPool<T>.Shared.Rent(length) : new T[length];

    private int AddState(
        StringOrMemory name,
        StringOrMemory localName,
        StringOrMemory prefix,
        StringOrMemory namespaceUri,
        NodeFlags flags,
        CompactNodeKind kind
    )
    {
        return _columns.Add(name, localName, prefix, namespaceUri, flags, kind);
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
        return new CompactSourceLocation(
            position.Index,
            checked((ushort)position.Line),
            checked((ushort)position.Column)
        );
    }

    public void Dispose()
    {
        _nodes.Dispose();
        _attributeWrappers?.Dispose();
        _attributes?.Dispose();
        _payloads?.Dispose();
        _columns.Dispose();
    }

    public void RemoveFromParent(int child) => Detach(child);

    public void RemoveChild(int parent, int child)
    {
        if (_columns.Parents[child] == parent)
            Detach(child);
    }

    public void ClearChildren(int parent)
    {
        var child = _columns.FirstChildren[parent];
        while (child >= 0)
        {
            var next = _columns.NextSiblings[child];
            _columns.Parents[child] = -1;
            _columns.PreviousSiblings[child] = -1;
            _columns.NextSiblings[child] = -1;
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
        var existing = GetAttribute(handle, name);
        if (existing is not null)
        {
            existing.Value = value;
            return;
        }

        _attributes ??= new PooledValueBuffer<MutableAttribute>(
            _pooled,
            ValidateCapacity(_hints.InitialAttributeCapacity, nameof(CompactParserHints.InitialAttributeCapacity))
        );
        _attributeWrappers ??= _pooled
            ? new PooledReferenceBuffer<ArenaAttribute>()
            : new ListReferenceBuffer<ArenaAttribute>();
        var payloadIndex = EnsurePayload(handle);
        ref var payload = ref _payloads![payloadIndex];
        var attributeHandle = _attributes.Add(new MutableAttribute(name, value));
        var wrapper = new ArenaAttribute(this, attributeHandle);
        _attributeWrappers.Add(wrapper);
        if (payload.FirstAttribute < 0)
            payload.FirstAttribute = attributeHandle;
        else
            _attributes[payload.LastAttribute].Next = attributeHandle;
        payload.LastAttribute = attributeHandle;
        payload.AttributeCount++;
    }

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
            _payloads![payload].Value = value;
        }
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
            _pooled,
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
    }
}

internal struct MutableNodePayload
{
    public MutableNodePayload()
    {
        FirstAttribute = -1;
        LastAttribute = -1;
    }

    public StringOrMemory Value;
    public int FirstAttribute;
    public int LastAttribute;
    public ushort AttributeCount;
}

internal struct MutableAttribute(StringOrMemory name, StringOrMemory value)
{
    public StringOrMemory Name = name;
    public StringOrMemory Value = value;
    public int Next = -1;
}

internal sealed class PooledValueBuffer<T> : IDisposable
{
    private readonly bool _pooled;
    private T[] _items;

    public PooledValueBuffer(bool pooled, int initialCapacity)
    {
        _pooled = pooled;
        _items = pooled ? ArrayPool<T>.Shared.Rent(initialCapacity) : new T[initialCapacity];
    }

    public int Count { get; private set; }
    public ref T this[int index] => ref _items[index];

    public int Add(T item)
    {
        if (Count == _items.Length)
            Grow();
        var index = Count++;
        _items[index] = item;
        return index;
    }

    public void AddRange(ReadOnlySpan<T> items)
    {
        EnsureCapacity(checked(Count + items.Length));
        items.CopyTo(_items.AsSpan(Count));
        Count += items.Length;
    }

    public (T[] Items, int Count) Detach()
    {
        var items = _items;
        var count = Count;
        _items = [];
        Count = 0;
        return (items, count);
    }

    public void Dispose()
    {
        if (_pooled && _items.Length != 0)
            ArrayPool<T>.Shared.Return(_items, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        _items = [];
        Count = 0;
    }

    private void Grow()
    {
        EnsureCapacity(checked(_items.Length * 2));
    }

    private void EnsureCapacity(int capacity)
    {
        if (capacity <= _items.Length)
            return;
        var nextCapacity = _items.Length;
        while (nextCapacity < capacity)
            nextCapacity = checked(nextCapacity * 2);
        if (!_pooled)
        {
            Array.Resize(ref _items, nextCapacity);
            return;
        }
        var next = ArrayPool<T>.Shared.Rent(nextCapacity);
        _items.AsSpan(0, Count).CopyTo(next);
        ArrayPool<T>.Shared.Return(_items, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        _items = next;
    }
}

internal sealed class MutableNodeColumns : IDisposable
{
    private readonly bool _pooled;
    private int _count;

    public MutableNodeColumns(bool pooled, int initialCapacity, bool trackSourceReferences)
    {
        if (initialCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));
        _pooled = pooled;
        Names = Allocate<StringOrMemory>(initialCapacity);
        Flags = Allocate<NodeFlags>(initialCapacity);
        Kinds = Allocate<CompactNodeKind>(initialCapacity);
        Parents = Allocate<int>(initialCapacity);
        FirstChildren = Allocate<int>(initialCapacity);
        LastChildren = Allocate<int>(initialCapacity);
        PreviousSiblings = Allocate<int>(initialCapacity);
        NextSiblings = Allocate<int>(initialCapacity);
        ChildCounts = Allocate<int>(initialCapacity);
        PayloadIndexes = Allocate<int>(initialCapacity);
        SourceReferences = trackSourceReferences ? Allocate<ISourceReference?>(initialCapacity) : null;
    }

    public StringOrMemory[] Names;
    public NodeFlags[] Flags;
    public CompactNodeKind[] Kinds;
    public int[] Parents;
    public int[] FirstChildren;
    public int[] LastChildren;
    public int[] PreviousSiblings;
    public int[] NextSiblings;
    public int[] ChildCounts;
    public int[]? TemplateFirstChildren;
    public int[] PayloadIndexes;
    public ISourceReference?[]? SourceReferences;

    public int Add(
        StringOrMemory name,
        StringOrMemory localName,
        StringOrMemory prefix,
        StringOrMemory namespaceUri,
        NodeFlags flags,
        CompactNodeKind kind
    )
    {
        EnsureCapacity();
        var handle = _count++;
        Names[handle] = name;
        Flags[handle] = flags;
        Kinds[handle] = kind;
        Parents[handle] = -1;
        FirstChildren[handle] = -1;
        LastChildren[handle] = -1;
        PreviousSiblings[handle] = -1;
        NextSiblings[handle] = -1;
        if (TemplateFirstChildren is not null)
            TemplateFirstChildren[handle] = -1;
        PayloadIndexes[handle] = -1;
        return handle;
    }

    public void Dispose()
    {
        if (!_pooled)
            return;
        Return(Names, true);
        Return(Flags, false);
        Return(Kinds, false);
        Return(Parents, false);
        Return(FirstChildren, false);
        Return(LastChildren, false);
        Return(PreviousSiblings, false);
        Return(NextSiblings, false);
        Return(ChildCounts, false);
        if (TemplateFirstChildren is not null)
            Return(TemplateFirstChildren, false);
        Return(PayloadIndexes, false);
        if (SourceReferences is not null)
            Return(SourceReferences, true);
    }

    private void EnsureCapacity()
    {
        if (_count < Names.Length)
            return;
        var size = checked(Names.Length * 2);
        Grow(ref Names, size, true);
        Grow(ref Flags, size, false);
        Grow(ref Kinds, size, false);
        Grow(ref Parents, size, false);
        Grow(ref FirstChildren, size, false);
        Grow(ref LastChildren, size, false);
        Grow(ref PreviousSiblings, size, false);
        Grow(ref NextSiblings, size, false);
        Grow(ref ChildCounts, size, false);
        if (TemplateFirstChildren is not null)
            Grow(ref TemplateFirstChildren, size, false);
        Grow(ref PayloadIndexes, size, false);
        if (SourceReferences is not null)
            Grow(ref SourceReferences, size, true);
    }

    public int TemplateFirstChild(int handle) => TemplateFirstChildren?[handle] ?? -1;

    public void SetTemplateFirstChild(int handle, int child)
    {
        if (TemplateFirstChildren is null)
        {
            TemplateFirstChildren = Allocate<int>(Names.Length);
            TemplateFirstChildren.AsSpan(0, _count).Fill(-1);
        }
        TemplateFirstChildren[handle] = child;
    }

    private T[] Allocate<T>(int capacity) => _pooled ? ArrayPool<T>.Shared.Rent(capacity) : new T[capacity];

    private void Grow<T>(ref T[] values, int capacity, bool clear)
    {
        if (!_pooled)
        {
            Array.Resize(ref values, capacity);
            return;
        }
        var next = ArrayPool<T>.Shared.Rent(capacity);
        values.AsSpan(0, _count).CopyTo(next);
        ArrayPool<T>.Shared.Return(values, clearArray: clear);
        values = next;
    }

    private static void Return<T>(T[] values, bool clear) => ArrayPool<T>.Shared.Return(values, clearArray: clear);
}

internal class ArenaNode : IConstructableNode, IConstructableNodeList
{
    protected internal ArenaNode(Arena arena, int handle)
    {
        Arena = arena;
        NodeHandle = handle;
    }

    internal Arena Arena { get; }
    internal int NodeHandle { get; }
    public StringOrMemory NodeName => Arena.Name(NodeHandle);
    public NodeFlags Flags => Arena.Flags(NodeHandle);
    public IConstructableNode? Parent
    {
        get
        {
            var parent = Arena.Parent(NodeHandle);
            return parent < 0 ? null : Arena.Node(parent);
        }
        set
        {
            if (value is ArenaNode node)
                Arena.AddChild(node.NodeHandle, NodeHandle);
            else
                Arena.RemoveFromParent(NodeHandle);
        }
    }
    public IConstructableNodeList ChildNodes => this;
    public int Length => Arena.ChildCount(NodeHandle);
    public IConstructableNode this[int index] => Arena.Node(Arena.ChildAt(NodeHandle, index));

    public void AddNode(IConstructableNode node) => Arena.AddChild(NodeHandle, ((ArenaNode)node).NodeHandle);

    public void InsertNode(int index, IConstructableNode node) =>
        Arena.AddChild(NodeHandle, ((ArenaNode)node).NodeHandle, index);

    public void AppendText(StringOrMemory text, bool emitWhiteSpaceOnly = false) =>
        Arena.AddText(NodeHandle, text, emitWhiteSpaceOnly);

    public void InsertText(int index, StringOrMemory text, bool emitWhiteSpaceOnly = false) =>
        Arena.AddText(NodeHandle, text, emitWhiteSpaceOnly, index);

    public void AddComment(ref StructHtmlToken token) => Arena.AddComment(NodeHandle, ref token);

    public void RemoveFromParent() => Arena.RemoveFromParent(NodeHandle);

    public void RemoveChild(IConstructableNode childNode)
    {
        var child = (ArenaNode)childNode;
        Arena.RemoveChild(NodeHandle, child.NodeHandle);
    }

    public void RemoveNode(int index, IConstructableNode childNode)
    {
        if (Arena.ChildAt(NodeHandle, index) != ((ArenaNode)childNode).NodeHandle)
            throw new ArgumentException(
                "The supplied node does not match the child at the requested index.",
                nameof(childNode)
            );
        Arena.RemoveChild(NodeHandle, ((ArenaNode)childNode).NodeHandle);
    }

    public void Clear() => Arena.ClearChildren(NodeHandle);

    public IEnumerator<IConstructableNode> GetEnumerator()
    {
        for (var index = 0; index < Arena.ChildCount(NodeHandle); index++)
            yield return Arena.Node(Arena.ChildAt(NodeHandle, index));
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal class ArenaElement : ArenaNode, IConstructableElement
{
    private ArenaNamedNodeMap? _attributes;

    public ArenaElement(Arena arena, int handle)
        : base(arena, handle) { }

    public StringOrMemory NamespaceUri => Arena.NamespaceUri(NodeHandle);
    public StringOrMemory LocalName => Arena.LocalName(NodeHandle);
    public StringOrMemory Prefix => Arena.Prefix(NodeHandle);
    public IConstructableNamedNodeMap Attributes => _attributes ??= new ArenaNamedNodeMap(Arena, NodeHandle);
    public ISourceReference? SourceReference
    {
        get => Arena.SourceReference(NodeHandle);
        set => Arena.SetSourceReference(NodeHandle, value);
    }

    public void SetAttribute(string? _, StringOrMemory name, StringOrMemory value) => SetOwnAttribute(name, value);

    public void SetOwnAttribute(StringOrMemory name, StringOrMemory value) =>
        Arena.SetOwnAttribute(NodeHandle, name, value);

    public StringOrMemory GetAttribute(StringOrMemory _, StringOrMemory name)
    {
        return Arena.GetAttribute(NodeHandle, name)?.Value ?? StringOrMemory.Empty;
    }

    public void SetAttributes(StructAttributes attributes)
    {
        for (var i = 0; i < attributes.Count; i++)
            SetOwnAttribute(attributes[i].Name, attributes[i].Value);
    }

    public bool HasAttribute(StringOrMemory name) => Arena.GetAttribute(NodeHandle, name) is not null;

    public void SetupElement() { }

    public virtual IConstructableNode ShallowCopy()
    {
        var copy = Arena.CreateElement(LocalName, Prefix, NamespaceUri, Flags);
        Arena.CopyAttributes(NodeHandle, copy.NodeHandle);
        return copy;
    }
}

internal sealed class ArenaDocument : ArenaElement, IConstructableDocument, IDisposable
{
    public ArenaDocument(Arena arena, int handle, TextSource source)
        : base(arena, handle) => Source = source;

    public TextSource Source { get; }
    public IDisposable? Builder { get; set; }
    public QuirksMode QuirksMode { get; set; }
    public bool IsLoading => false;
    public IConstructableElement DocumentElement => ChildNodes.OfType<IConstructableElement>().First();
    public IConstructableElement? Head =>
        DocumentElement
            .ChildNodes.OfType<IConstructableElement>()
            .FirstOrDefault(element => element.LocalName.Equals(TagNames.Head));

    public void PerformMicrotaskCheckpoint() { }

    public void ProvideStableState() { }

    public void TrackError(Exception exception) { }

    public Task WaitForReadyAsync(CancellationToken cancelToken) => Task.CompletedTask;

    public Task FinishLoadingAsync() => Task.CompletedTask;

    public void ApplyManifest() { }

    public void Dispose()
    {
        try
        {
            Builder?.Dispose();
            Source.Dispose();
        }
        finally
        {
            Arena.Dispose();
        }
    }
}

internal sealed class ArenaTemplateElement(Arena arena, int handle)
    : ArenaElement(arena, handle),
        IConstructableTemplateElement
{
    public void PopulateFragment()
    {
        Arena.PopulateTemplate(NodeHandle);
    }
}

internal sealed class ArenaScriptElement(Arena arena, int handle)
    : ArenaElement(arena, handle),
        IConstructableScriptElement
{
    public Task RunAsync(CancellationToken cancel) => Task.CompletedTask;

    public bool Prepare(IConstructableDocument document) => false;
}

internal sealed class ArenaMetaElement(Arena arena, int handle) : ArenaElement(arena, handle), IConstructableMetaElement
{
    public void Handle() { }
}

internal sealed class ArenaFormElement(Arena arena, int handle)
    : ArenaElement(arena, handle),
        IConstructableFormElement;

internal sealed class ArenaFrameElement(Arena arena, int handle)
    : ArenaElement(arena, handle),
        IConstructableFrameElement;

internal sealed class ArenaMathElement(Arena arena, int handle)
    : ArenaElement(arena, handle),
        IConstructableMathElement;

internal sealed class ArenaSvgElement(Arena arena, int handle) : ArenaElement(arena, handle), IConstructableSvgElement;

internal sealed class ArenaAttribute(Arena arena, int handle) : IConstructableAttr
{
    public StringOrMemory Name => arena.AttributeName(handle);
    public StringOrMemory Value
    {
        get => arena.AttributeValue(handle);
        set => arena.SetAttributeValue(handle, value);
    }
}

internal sealed class ArenaNamedNodeMap(Arena arena, int handle) : IConstructableNamedNodeMap
{
    public IConstructableAttr? this[StringOrMemory name] => arena.GetAttribute(handle, name);
    public int Length => arena.AttributeCount(handle);

    public bool SameAs(IConstructableNamedNodeMap? other) =>
        other is not null
        && Length == other.Length
        && arena.Attributes(handle).All(attribute => other[attribute.Name]?.Value == attribute.Value);
}

internal interface IReferenceBuffer<T> : IDisposable
    where T : class
{
    int Count { get; }
    T this[int index] { get; }
    void Add(T item);
}

internal sealed class ListReferenceBuffer<T> : IReferenceBuffer<T>
    where T : class
{
    private readonly List<T> _items = [];
    public int Count => _items.Count;
    public T this[int index] => _items[index];

    public void Add(T item) => _items.Add(item);

    public void Dispose() { }
}

internal sealed class PooledReferenceBuffer<T> : IReferenceBuffer<T>
    where T : class
{
    private T[] _items = ArrayPool<T>.Shared.Rent(64);
    public int Count { get; private set; }
    public T this[int index] => index < Count ? _items[index] : throw new ArgumentOutOfRangeException(nameof(index));

    public void Add(T item)
    {
        if (Count == _items.Length)
            Grow();
        _items[Count++] = item;
    }

    public void Dispose()
    {
        var items = _items;
        if (items.Length == 0)
            return;
        _items = [];
        Count = 0;
        ArrayPool<T>.Shared.Return(items, clearArray: true);
    }

    private void Grow()
    {
        var next = ArrayPool<T>.Shared.Rent(checked(_items.Length * 2));
        _items.AsSpan(0, Count).CopyTo(next);
        ArrayPool<T>.Shared.Return(_items, clearArray: true);
        _items = next;
    }
}

internal sealed class NameTable
{
#if NET10_0
    private readonly Dictionary<string, ushort> _ids = new(StringComparer.Ordinal);
#else
    private readonly Dictionary<StringOrMemory, ushort> _ids = [];
#endif
    private readonly List<string> _names = [];

    public int Count => _names.Count;

    public ushort GetId(StringOrMemory name)
    {
#if NET10_0
        var lookup = _ids.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(name.Memory.Span, out var id))
            return id;
        var ownedName = name.ToString();
        id = checked((ushort)_names.Count);
        _ids.Add(ownedName, id);
        _names.Add(ownedName);
#else
        if (_ids.TryGetValue(name, out var id))
            return id;
        id = checked((ushort)_names.Count);
        _ids.Add(name, id);
        _names.Add(name.ToString());
#endif
        return id;
    }

    public void CopyTo(string[] destination) => _names.CopyTo(destination);
}

internal enum ElementMarker
{
    None,
    Template,
    Script,
    Meta,
    Form,
    Frame,
    Math,
    Svg,
}

internal sealed class ArenaConstructionFactory : IDomConstructionElementFactory<ArenaDocument, ArenaElement>
{
    private readonly bool _pooledScratch;
    private readonly CompactParserHints _hints;
    private readonly bool _trackSourceReferences;

    public ArenaConstructionFactory(bool pooledScratch, CompactParserHints hints, bool trackSourceReferences)
    {
        _pooledScratch = pooledScratch;
        _hints = hints;
        _trackSourceReferences = trackSourceReferences;
    }

    public ArenaElement Create(
        ArenaDocument document,
        StringOrMemory localName,
        StringOrMemory prefix = default,
        NodeFlags flags = NodeFlags.None
    )
    {
        var canonical =
            localName.Memory.Span.IndexOf('-') >= 0 ? NodeFlags.None : GeneratedTagMetadata.GetFlags(localName);
        return document.Arena.CreateElement(
            localName,
            prefix,
            NamespaceNames.HtmlUri,
            flags | canonical | NodeFlags.HtmlMember
        );
    }

    public ArenaElement CreateNoScript(ArenaDocument document, bool scripting) => Create(document, TagNames.NoScript);

    public IConstructableNode CreateDocumentType(
        ArenaDocument document,
        StringOrMemory name,
        StringOrMemory publicIdentifier,
        StringOrMemory systemIdentifier
    ) => document.Arena.CreateLeaf(name, default, CompactNodeKind.Other);

    public IConstructableMathElement CreateMath(ArenaDocument document, StringOrMemory name = default) =>
        (IConstructableMathElement)
            document.Arena.CreateElement(
                name,
                default,
                NamespaceNames.MathMlUri,
                NodeFlags.MathMember,
                ElementMarker.Math
            );

    public IConstructableSvgElement CreateSvg(ArenaDocument document, StringOrMemory name = default) =>
        (IConstructableSvgElement)
            document.Arena.CreateElement(name, default, NamespaceNames.SvgUri, NodeFlags.SvgMember, ElementMarker.Svg);

    public IConstructableMetaElement CreateMeta(ArenaDocument document) =>
        (IConstructableMetaElement)
            document.Arena.CreateElement(
                TagNames.Meta,
                default,
                NamespaceNames.HtmlUri,
                GeneratedTagMetadata.GetFlags(TagNames.Meta) | NodeFlags.HtmlMember,
                ElementMarker.Meta
            );

    public IConstructableScriptElement CreateScript(ArenaDocument document, bool parserInserted, bool started) =>
        (IConstructableScriptElement)
            document.Arena.CreateElement(
                TagNames.Script,
                default,
                NamespaceNames.HtmlUri,
                GeneratedTagMetadata.GetFlags(TagNames.Script) | NodeFlags.HtmlMember,
                ElementMarker.Script
            );

    public IConstructableFrameElement CreateFrame(ArenaDocument document) =>
        (IConstructableFrameElement)
            document.Arena.CreateElement(
                TagNames.Frame,
                default,
                NamespaceNames.HtmlUri,
                GeneratedTagMetadata.GetFlags(TagNames.Frame) | NodeFlags.HtmlMember,
                ElementMarker.Frame
            );

    public IConstructableTemplateElement CreateTemplate(ArenaDocument document) =>
        (IConstructableTemplateElement)
            document.Arena.CreateElement(
                TagNames.Template,
                default,
                NamespaceNames.HtmlUri,
                GeneratedTagMetadata.GetFlags(TagNames.Template) | NodeFlags.HtmlMember,
                ElementMarker.Template
            );

    public IConstructableFormElement CreateForm(ArenaDocument document) =>
        (IConstructableFormElement)
            document.Arena.CreateElement(
                TagNames.Form,
                default,
                NamespaceNames.HtmlUri,
                GeneratedTagMetadata.GetFlags(TagNames.Form) | NodeFlags.HtmlMember,
                ElementMarker.Form
            );

    public ArenaElement CreateUnknown(ArenaDocument document, StringOrMemory tagName) => Create(document, tagName);

    public ArenaDocument CreateDocument(TextSource source, IBrowsingContext? context = null) =>
        new Arena(_pooledScratch, _hints, _trackSourceReferences).CreateDocument(source);
}
