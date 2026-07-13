using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom.CompactPrototype.Arena;

internal sealed class NameTable
{
#if NET10_0
    private readonly Dictionary<string, ushort> _ids = new(StringComparer.Ordinal);
#else
    private readonly Dictionary<StringOrMemory, ushort> _ids = [];
#endif
    private List<string>? _customNames;

    public int CustomCount => _customNames?.Count ?? 0;

    public ushort GetId(StringOrMemory name)
    {
#if NET10_0
        var lookup = _ids.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(name.Memory.Span, out var id))
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
}
