using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom.CompactPrototype.Arena;

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
