using System.Text;

namespace AngleSharp.ReadOnlyDom.Streaming.Utf8Stream.Query;

public readonly ref struct CompletedElement
{
    private readonly ElementCapture _capture;
    private readonly string[] _attributeNames;
    private readonly byte[][] _attributeNamesUtf8;
    private readonly int[] _attributeIndexes;

    internal CompletedElement(
        ElementCapture capture,
        string[] attributeNames,
        byte[][] attributeNamesUtf8,
        int[] attributeIndexes
    )
    {
        _capture = capture;
        _attributeNames = attributeNames;
        _attributeNamesUtf8 = attributeNamesUtf8;
        _attributeIndexes = attributeIndexes;
    }

    /// <summary>Borrowed normalized or raw UTF-8 text, valid only during the callback.</summary>
    public ReadOnlySpan<byte> TextUtf8 => _capture.TextUtf8;

    /// <summary>Returns an owned UTF-16 string, decoding only when requested.</summary>
    public string GetText() => _capture.GetText();

    public bool TryGetAttributeUtf8(string name, out ReadOnlySpan<byte> value)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (var index = 0; index < _attributeIndexes.Length; index++)
        {
            if (_attributeNames[_attributeIndexes[index]].Equals(name, StringComparison.OrdinalIgnoreCase))
                return _capture.TryGetAttribute(index, out value);
        }
        value = default;
        return false;
    }

    /// <summary>The UTF-8 attribute name must use the normalized lowercase spelling compiled by the query.</summary>
    public bool TryGetAttributeUtf8(ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)
    {
        for (var index = 0; index < _attributeIndexes.Length; index++)
        {
            if (_attributeNamesUtf8[_attributeIndexes[index]].AsSpan().SequenceEqual(name))
                return _capture.TryGetAttribute(index, out value);
        }
        value = default;
        return false;
    }

    public string? GetAttribute(string name) =>
        TryGetAttributeUtf8(name, out var value) ? Encoding.UTF8.GetString(value) : null;

    public string GetAttributeOrEmpty(string name) => GetAttribute(name) ?? string.Empty;
}
