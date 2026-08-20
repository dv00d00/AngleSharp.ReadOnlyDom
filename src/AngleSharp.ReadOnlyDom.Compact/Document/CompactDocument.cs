using System.Buffers;
using System.Runtime.CompilerServices;
using AngleSharp.Text;
using ArenaStorage = AngleSharp.ReadOnlyDom.Compact.Arena.Arena;

namespace AngleSharp.ReadOnlyDom.Compact.Document;

public sealed partial class CompactDocument : IDisposable
{
    private readonly ArenaStorage? _arena;
    private readonly CompactAttribute[]? _attributes;
    private readonly bool _hasTemplates;
    private readonly CompactMetadataOptions _metadataOptions;
    private readonly int _nameCount;
    private readonly int _nodeCount;
    private readonly int _attributeCount;

    private readonly string[] _names;
    private readonly CompactNode[]? _nodes;
    private readonly int[]? _parents;
    private readonly CompactNodePayload[]? _payloads;
    private readonly TextSource? _source;
    private readonly CompactSourceLocation[]? _sources;
    private readonly CompactTemplateBoundary[] _templateBoundaries;
    private readonly char[]? _text;
    private int _disposed;

    internal CompactDocument(
        CompactNode[] nodes,
        CompactNodePayload[] payloads,
        CompactAttribute[] attributes,
        string[] names,
        char[] text,
        int[]? parents,
        CompactSourceLocation[]? sources,
        CompactTemplateBoundary[] templateBoundaries,
        int nodeCount,
        int payloadCount,
        int attributeCount,
        int nameCount,
        int textLength
    )
    {
        _nodes = nodes;
        _payloads = payloads;
        _attributes = attributes;
        _names = names;
        _text = text;
        _parents = parents;
        _sources = sources;
        _templateBoundaries = templateBoundaries;
        _nodeCount = nodeCount;
        PayloadCount = payloadCount;
        _attributeCount = attributeCount;
        _nameCount = nameCount;
        TextLength = textLength;
        _hasTemplates = templateBoundaries.Length != 0;
    }

    internal CompactDocument(
        ArenaStorage arena,
        TextSource source,
        string[] names,
        int nodeCount,
        int payloadCount,
        int attributeCount,
        int nameCount,
        int textLength,
        CompactMetadataOptions metadataOptions,
        CompactTemplateBoundary[] templateBoundaries,
        char[] text
    )
    {
        _arena = arena;
        _source = source;
        _names = names;
        _nodeCount = nodeCount;
        PayloadCount = payloadCount;
        _attributeCount = attributeCount;
        _nameCount = nameCount;
        TextLength = textLength;
        _metadataOptions = metadataOptions;
        _templateBoundaries = templateBoundaries;
        _text = text;
        _hasTemplates = templateBoundaries.Length != 0;
    }

    internal CompactDocumentLayout Layout =>
        _arena is null ? CompactDocumentLayout.Packed : CompactDocumentLayout.FrozenColumns;

    public int NodeCount
    {
        get
        {
            ThrowIfDisposed();
            return _nodeCount;
        }
    }

    public int AttributeCount
    {
        get
        {
            ThrowIfDisposed();
            return _attributeCount;
        }
    }

    internal int RawNodeCount => _nodeCount;

    internal int PayloadCount { get; }

    internal int TextLength { get; }

    public bool HasParentLinks
    {
        get
        {
            ThrowIfDisposed();
            return RetainsParentLinks;
        }
    }

    public bool HasSourceLocations
    {
        get
        {
            ThrowIfDisposed();
            return RetainsSourceLocations;
        }
    }

    private bool RetainsParentLinks =>
        _arena is null ? _parents is not null : _metadataOptions.HasFlag(CompactMetadataOptions.ParentLinks);

    private bool RetainsSourceLocations =>
        _arena is null ? _sources is not null : _metadataOptions.HasFlag(CompactMetadataOptions.SourceLocations);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(CompactDocument));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_names.Length != 0)
            ArrayPool<string>.Shared.Return(_names, true);
        if (_arena is not null)
        {
            try
            {
                _arena.Dispose();
            }
            finally
            {
                try
                {
                    _source!.Dispose();
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(_text!);
                }
            }

            return;
        }

        ArrayPool<CompactNode>.Shared.Return(_nodes!);
        ArrayPool<CompactNodePayload>.Shared.Return(_payloads!);
        ArrayPool<CompactAttribute>.Shared.Return(_attributes!);
        ArrayPool<char>.Shared.Return(_text!);
        if (_parents is not null)
            ArrayPool<int>.Shared.Return(_parents);
        if (_sources is not null)
            ArrayPool<CompactSourceLocation>.Shared.Return(_sources);
    }
}
