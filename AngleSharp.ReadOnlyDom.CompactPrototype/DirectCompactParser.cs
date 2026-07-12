using System.Buffers;
using System.Collections;
using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.CompactPrototype;

public static class DirectCompactParser
{
    public static HotCompactDocument Parse(
        string html,
        CompactMetadataOptions options = CompactMetadataOptions.None,
        CompactBufferOwnership ownership = CompactBufferOwnership.Owned
    )
    {
        var configuration = Configuration.Default.With(_ => new ArenaConstructionFactory(
            ownership == CompactBufferOwnership.Pooled
        ));
        var context = BrowsingContext.New(configuration);
        var parser = new HtmlParser(
            new HtmlParserOptions
            {
                SkipComments = true,
                SkipProcessingInstructions = true,
                IsKeepingSourceReferences = options.HasFlag(CompactMetadataOptions.SourceLocations),
            },
            context
        );
        var source = new TextSource(new StringTextSource(html));
        var document = parser.ParseDocument<ArenaDocument, ArenaElement>(source);
        try
        {
            return document.Arena.Finalize(document.NodeHandle, options, ownership);
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
    private readonly IReferenceBuffer<NodeState> _states;

    public Arena(bool pooled)
    {
        _nodes = pooled ? new PooledReferenceBuffer<ArenaNode>() : new ListReferenceBuffer<ArenaNode>();
        _states = pooled ? new PooledReferenceBuffer<NodeState>() : new ListReferenceBuffer<NodeState>();
    }

    public ArenaDocument CreateDocument(TextSource source)
    {
        var state = new NodeState("#document", "#document", default, default, NodeFlags.None, CompactNodeKind.Document);
        var document = new ArenaDocument(this, AddState(state), source);
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
        var state = new NodeState(qualifiedName, name, prefix, namespaceUri, flags, CompactNodeKind.Element);
        var handle = AddState(state);
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
        var handle = AddState(new NodeState(name, name, default, default, NodeFlags.None, kind) { Value = value });
        var node = new ArenaNode(this, handle);
        _nodes.Add(node);
        return node;
    }

    public ArenaNode Node(int handle) => _nodes[handle];

    public NodeState State(int handle) => _states[handle];

    public void AddChild(int parent, int child, int? index = null)
    {
        var childState = State(child);
        if (childState.Parent >= 0)
            State(childState.Parent).Children.Remove(child);
        childState.Parent = parent;
        var children = State(parent).Children;
        if (index.HasValue)
            children.Insert(index.Value, child);
        else
            children.Add(child);
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
        var order = new List<int>(_nodes.Count);
        AddPreOrder(root, order);
        var remap = new int[_nodes.Count];
        Array.Fill(remap, -1);
        for (var i = 0; i < order.Count; i++)
            remap[order[i]] = i;

        var attributeCount = 0;
        var textLength = 0;
        var payloadCount = 0;
        foreach (var oldHandle in order)
        {
            var state = State(oldHandle);
            attributeCount += state.Attributes.Count;
            textLength += state.Value.Length;
            foreach (var attribute in state.Attributes)
                textLength += attribute.Value.Length;
            if (state.Attributes.Count != 0 || state.Value.Length != 0)
                payloadCount++;
        }

        var pooled = ownership == CompactBufferOwnership.Pooled;
        var nodes = Allocate<HotCompactNode>(order.Count, pooled);
        var payloads = Allocate<ColdNodePayload>(payloadCount, pooled);
        var attributes = Allocate<CompactAttribute>(attributeCount, pooled);
        var text = Allocate<char>(textLength, pooled);
        var parents = options.HasFlag(CompactMetadataOptions.ParentLinks) ? Allocate<int>(order.Count, pooled) : null;
        var sources = options.HasFlag(CompactMetadataOptions.SourceLocations)
            ? Allocate<CompactSourceLocation>(order.Count, pooled)
            : null;
        var names = new NameTable();
        var attributeIndex = 0;
        var payloadIndex = 0;
        var textIndex = 0;

        for (var handle = 0; handle < order.Count; handle++)
        {
            var state = State(order[handle]);
            var children = FinalChildren(state);
            var firstChild = children.Count == 0 ? -1 : remap[children[0]];
            var nextSibling = -1;
            if (state.Parent >= 0)
            {
                var siblings = FinalChildren(State(state.Parent));
                var siblingIndex = siblings.IndexOf(order[handle]);
                if (siblingIndex >= 0 && siblingIndex + 1 < siblings.Count)
                    nextSibling = remap[siblings[siblingIndex + 1]];
            }

            var nodePayload = -1;
            if (state.Attributes.Count != 0 || state.Value.Length != 0)
            {
                var firstAttribute = attributeIndex;
                foreach (var attribute in state.Attributes)
                {
                    var value = CopyText(attribute.Value, text, ref textIndex);
                    attributes[attributeIndex++] = new CompactAttribute(
                        names.GetId(attribute.Name),
                        value.Start,
                        value.Length
                    );
                }
                var nodeValue = CopyText(state.Value, text, ref textIndex);
                nodePayload = payloadIndex;
                payloads[payloadIndex++] = new ColdNodePayload(
                    firstAttribute,
                    nodeValue.Start,
                    nodeValue.Length,
                    checked((ushort)state.Attributes.Count)
                );
            }

            nodes[handle] = new HotCompactNode(
                firstChild,
                nextSibling,
                nodePayload,
                names.GetId(state.Name),
                state.Kind,
                (byte)state.Flags
            );
            if (parents is not null)
                parents[handle] = state.Parent < 0 ? -1 : remap[state.Parent];
            if (sources is not null)
                sources[handle] = GetSource(state.SourceReference);
        }

        var nameArray = Allocate<string>(names.Count, pooled);
        names.CopyTo(nameArray);
        return new HotCompactDocument(
            nodes,
            payloads,
            attributes,
            nameArray,
            text,
            parents,
            sources,
            order.Count,
            payloadCount,
            attributeCount,
            names.Count,
            textLength,
            pooled
        );
    }

    private static T[] Allocate<T>(int length, bool pooled) =>
        pooled ? ArrayPool<T>.Shared.Rent(length) : new T[length];

    private int AddState(NodeState state)
    {
        var handle = _states.Count;
        _states.Add(state);
        return handle;
    }

    private void AddPreOrder(int handle, List<int> order)
    {
        order.Add(handle);
        foreach (var child in FinalChildren(State(handle)))
            AddPreOrder(child, order);
    }

    private static List<int> FinalChildren(NodeState state) => state.TemplateContent ?? state.Children;

    private static (int Start, int Length) CopyText(StringOrMemory value, char[] destination, ref int index)
    {
        if (value.Length == 0)
            return (-1, 0);
        var start = index;
        value.Memory.Span.CopyTo(destination.AsSpan(index));
        index += value.Length;
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
        _states.Dispose();
    }
}

internal sealed class NodeState
{
    public NodeState(
        StringOrMemory name,
        StringOrMemory localName,
        StringOrMemory prefix,
        StringOrMemory namespaceUri,
        NodeFlags flags,
        CompactNodeKind kind
    )
    {
        Name = name;
        LocalName = localName;
        Prefix = prefix;
        NamespaceUri = namespaceUri;
        Flags = flags;
        Kind = kind;
    }

    public StringOrMemory Name;
    public StringOrMemory LocalName;
    public StringOrMemory Prefix;
    public StringOrMemory NamespaceUri;
    public StringOrMemory Value;
    public NodeFlags Flags;
    public CompactNodeKind Kind;
    public int Parent = -1;
    public List<int> Children = [];
    public List<int>? TemplateContent;
    public List<ArenaAttribute> Attributes = [];
    public ISourceReference? SourceReference;
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
    protected NodeState State => Arena.State(NodeHandle);
    public StringOrMemory NodeName => State.Name;
    public NodeFlags Flags => State.Flags;
    public IConstructableNode? Parent
    {
        get => State.Parent < 0 ? null : Arena.Node(State.Parent);
        set => State.Parent = value is ArenaNode node ? node.NodeHandle : -1;
    }
    public IConstructableNodeList ChildNodes => this;
    public int Length => State.Children.Count;
    public IConstructableNode this[int index] => Arena.Node(State.Children[index]);

    public void AddNode(IConstructableNode node) => Arena.AddChild(NodeHandle, ((ArenaNode)node).NodeHandle);

    public void InsertNode(int index, IConstructableNode node) =>
        Arena.AddChild(NodeHandle, ((ArenaNode)node).NodeHandle, index);

    public void AppendText(StringOrMemory text, bool emitWhiteSpaceOnly = false) =>
        Arena.AddText(NodeHandle, text, emitWhiteSpaceOnly);

    public void InsertText(int index, StringOrMemory text, bool emitWhiteSpaceOnly = false) =>
        Arena.AddText(NodeHandle, text, emitWhiteSpaceOnly, index);

    public void AddComment(ref StructHtmlToken token) => Arena.AddComment(NodeHandle, ref token);

    public void RemoveFromParent()
    {
        if (State.Parent >= 0)
            Arena.State(State.Parent).Children.Remove(NodeHandle);
        State.Parent = -1;
    }

    public void RemoveChild(IConstructableNode childNode)
    {
        var child = (ArenaNode)childNode;
        if (State.Children.Remove(child.NodeHandle))
            child.State.Parent = -1;
    }

    public void RemoveNode(int index, IConstructableNode childNode)
    {
        State.Children.RemoveAt(index);
        ((ArenaNode)childNode).State.Parent = -1;
    }

    public void Clear()
    {
        foreach (var child in State.Children)
            Arena.State(child).Parent = -1;
        State.Children.Clear();
    }

    public IEnumerator<IConstructableNode> GetEnumerator()
    {
        foreach (var child in State.Children)
            yield return Arena.Node(child);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal class ArenaElement : ArenaNode, IConstructableElement
{
    private ArenaNamedNodeMap? _attributes;

    public ArenaElement(Arena arena, int handle)
        : base(arena, handle) { }

    public StringOrMemory NamespaceUri => State.NamespaceUri;
    public StringOrMemory LocalName => State.LocalName;
    public StringOrMemory Prefix => State.Prefix;
    public IConstructableNamedNodeMap Attributes => _attributes ??= new ArenaNamedNodeMap(State.Attributes);
    public ISourceReference? SourceReference
    {
        get => State.SourceReference;
        set => State.SourceReference = value;
    }

    public void SetAttribute(string? _, StringOrMemory name, StringOrMemory value) => SetOwnAttribute(name, value);

    public void SetOwnAttribute(StringOrMemory name, StringOrMemory value)
    {
        foreach (var attribute in State.Attributes)
        {
            if (attribute.Name == name)
            {
                attribute.Value = value;
                return;
            }
        }
        State.Attributes.Add(new ArenaAttribute(name, value));
    }

    public StringOrMemory GetAttribute(StringOrMemory _, StringOrMemory name)
    {
        foreach (var attribute in State.Attributes)
            if (attribute.Name == name)
                return attribute.Value;
        return StringOrMemory.Empty;
    }

    public void SetAttributes(StructAttributes attributes)
    {
        for (var i = 0; i < attributes.Count; i++)
            SetOwnAttribute(attributes[i].Name, attributes[i].Value);
    }

    public bool HasAttribute(StringOrMemory name) => State.Attributes.Any(attribute => attribute.Name == name);

    public void SetupElement() { }

    public virtual IConstructableNode ShallowCopy()
    {
        var copy = Arena.CreateElement(LocalName, Prefix, NamespaceUri, Flags);
        foreach (var attribute in State.Attributes)
            copy.SetOwnAttribute(attribute.Name, attribute.Value);
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
        State.TemplateContent = [.. State.Children];
        State.Children.Clear();
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

internal sealed class ArenaAttribute(StringOrMemory name, StringOrMemory value) : IConstructableAttr
{
    public StringOrMemory Name { get; } = name;
    public StringOrMemory Value { get; set; } = value;
}

internal sealed class ArenaNamedNodeMap(List<ArenaAttribute> attributes) : IConstructableNamedNodeMap
{
    public IConstructableAttr? this[StringOrMemory name] =>
        attributes.FirstOrDefault(attribute => attribute.Name == name);
    public int Length => attributes.Count;

    public bool SameAs(IConstructableNamedNodeMap? other) =>
        other is not null
        && attributes.Count == other.Length
        && attributes.All(attribute => other[attribute.Name]?.Value == attribute.Value);
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

    public ArenaConstructionFactory(bool pooledScratch)
    {
        _pooledScratch = pooledScratch;
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
        new Arena(_pooledScratch).CreateDocument(source);
}
