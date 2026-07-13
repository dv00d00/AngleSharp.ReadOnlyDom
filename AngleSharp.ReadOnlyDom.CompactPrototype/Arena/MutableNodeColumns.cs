using System.Buffers;
using AngleSharp.Dom;

namespace AngleSharp.ReadOnlyDom.CompactPrototype.Arena;

internal sealed class MutableNodeColumns : IDisposable
{
    private int _count;

    public MutableNodeColumns(int initialCapacity, bool trackSourceReferences)
    {
        if (initialCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));
        NameIds = Allocate<ushort>(initialCapacity);
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

    public ushort[] NameIds;
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

    public int Count => _count;

    public int Add(ushort nameId, NodeFlags flags, CompactNodeKind kind)
    {
        EnsureCapacity();
        var handle = _count++;
        NameIds[handle] = nameId;
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

    // Returns the columns that are only read during construction so a frozen arena does not retain them.
    public void ReleaseConstructionColumns()
    {
        Return(PreviousSiblings, false);
        PreviousSiblings = [];
        Return(LastChildren, false);
        LastChildren = [];
        Return(ChildCounts, false);
        ChildCounts = [];
    }

    public void Dispose()
    {
        Return(NameIds, false);
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
        if (_count < NameIds.Length)
            return;
        var size = checked(NameIds.Length * 2);
        Grow(ref NameIds, size, false);
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
            TemplateFirstChildren = Allocate<int>(NameIds.Length);
            TemplateFirstChildren.AsSpan(0, _count).Fill(-1);
        }
        TemplateFirstChildren[handle] = child;
    }

    private static T[] Allocate<T>(int capacity) => ArrayPool<T>.Shared.Rent(capacity);

    private void Grow<T>(ref T[] values, int capacity, bool clear)
    {
        var next = ArrayPool<T>.Shared.Rent(capacity);
        values.AsSpan(0, _count).CopyTo(next);
        ArrayPool<T>.Shared.Return(values, clearArray: clear);
        values = next;
    }

    private static void Return<T>(T[] values, bool clear)
    {
        if (values.Length != 0)
            ArrayPool<T>.Shared.Return(values, clearArray: clear);
    }
}
