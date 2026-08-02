using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed partial class Arena : IDisposable
{
    private static ReadOnlySpan<char> WhiteSpace => " \t\r\n";
    private readonly MutableNodeColumns _columns;
    private readonly NameTable _names = new();
    private readonly CompactParserHints _hints;
    private readonly ICompactConstructionViewState? _constructionView;
    private PooledValueBuffer<MutableNodePayload>? _payloads;
    private PooledValueBuffer<MutableAttribute>? _attributes;
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
        return document;
    }

    internal int CreateElementHandle(StringOrMemory name, StringOrMemory prefix, NodeFlags flags)
    {
        var qualifiedName = prefix.IsNullOrEmpty ? name : $"{prefix}:{name}";
        return AddState(qualifiedName, flags, CompactNodeKind.Element);
    }

    internal int CreateLeafHandle(StringOrMemory name, StringOrMemory value, CompactNodeKind kind) =>
        AddLeaf(name, value, kind);

    private int AddLeaf(StringOrMemory name, StringOrMemory value, CompactNodeKind kind)
    {
        var handle = AddState(name, NodeFlags.None, kind);
        SetValue(handle, value);
        return handle;
    }

    private int AddTextLeaf(StringOrMemory value)
    {
        _unattachedNodeCount++;
        _constructionView?.NodeMaterialized();
        var handle = _columns.Add(_textNameId, NodeFlags.None, CompactNodeKind.Text);
        SetValue(handle, value);
        return handle;
    }

    public StringOrMemory Name(int handle) => _names.GetName(_columns.NameIds[handle]);

    internal ushort NameId(int handle) => _columns.NameIds[handle];

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

    internal int NodeCount => _columns.Count;

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

    public ISourceReference? SourceReference(int handle) => _columns.SourceReferences?[handle];

    public void SetSourceReference(int handle, ISourceReference? value)
    {
        if (_columns.SourceReferences is not null)
            _columns.SourceReferences[handle] = value;
    }

    public void AddChild(int parent, int child, int? index = null)
    {
        if (_columns.Parents[child] >= 0 || child != _columns.Count - 1)
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

    private int AddState(StringOrMemory name, NodeFlags flags, CompactNodeKind kind)
    {
        _unattachedNodeCount++;
        _constructionView?.NodeMaterialized();
        return _columns.Add(_names.GetId(name), flags, kind);
    }

    public void Dispose()
    {
        _attributes?.Dispose();
        _payloads?.Dispose();
        _columns.Dispose();
    }

    public CompactAggregateResult CreateAggregateResult(int root, int inputBytesConsumed) =>
        (_constructionView as CompactAggregateExecutionState)?.CreateResult(this, root, inputBytesConsumed)
        ?? throw new InvalidOperationException("The arena was not configured for aggregate extraction.");

    public void SetTokensProcessed(int count) => _constructionView?.SetTokensProcessed(count);

    private static int ValidateCapacity(int capacity, string name) =>
        capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(name, "Capacity hints must be positive.");
}
