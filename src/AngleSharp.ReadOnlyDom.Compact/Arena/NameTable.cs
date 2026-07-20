using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed class NameTable
{
#if NET10_0
    private readonly Dictionary<string, ushort> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ushort>.AlternateLookup<ReadOnlySpan<char>> _lookup;
#else
    private readonly Dictionary<StringOrMemory, ushort> _ids = [];
#endif
    private List<string>? _customNames;

#if NET10_0
    public NameTable()
    {
        _lookup = _ids.GetAlternateLookup<ReadOnlySpan<char>>();
    }
#endif

    public int CustomCount => _customNames?.Count ?? 0;

    public ushort GetId(StringOrMemory name)
    {
#if NET10_0
        if (_lookup.TryGetValue(name.Memory.Span, out var id))
            return id;
        string ownedName;
        if (GeneratedTagMetadata.TryGetKnownNameId(name, out id))
        {
            ownedName = GeneratedTagMetadata.GetKnownName(id);
        }
        else
        {
            _customNames ??= [];
            ownedName = name.ToString();
            id = checked((ushort)(GeneratedTagMetadata.KnownNameCount + _customNames.Count));
            _customNames.Add(ownedName);
        }
        _ids.Add(ownedName, id);
#else
        if (_ids.TryGetValue(name, out var id))
            return id;
        string ownedName;
        if (GeneratedTagMetadata.TryGetKnownNameId(name, out id))
        {
            ownedName = GeneratedTagMetadata.GetKnownName(id);
        }
        else
        {
            _customNames ??= [];
            ownedName = name.ToString();
            id = checked((ushort)(GeneratedTagMetadata.KnownNameCount + _customNames.Count));
            _customNames.Add(ownedName);
        }
        _ids.Add(ownedName, id);
#endif
        return id;
    }

    public void CopyCustomNamesTo(string[] destination) => _customNames?.CopyTo(destination);

    public StringOrMemory GetName(ushort id)
    {
        if (id < GeneratedTagMetadata.KnownNameCount)
            return GeneratedTagMetadata.GetKnownName(id);
        else
            return _customNames![id - GeneratedTagMetadata.KnownNameCount];
    }

    public bool TryGetId(StringOrMemory name, out ushort id)
    {
        if (GeneratedTagMetadata.TryGetKnownNameId(name, out id))
            return true;
#if NET10_0
        return _lookup.TryGetValue(name.Memory.Span, out id);
#else
        return _ids.TryGetValue(name, out id);
#endif
    }
}
