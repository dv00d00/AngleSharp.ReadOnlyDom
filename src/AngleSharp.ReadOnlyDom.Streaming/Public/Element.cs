namespace AngleSharp.ReadOnlyDom.Streaming.Query;

public readonly ref struct Element
{
    private readonly string[] _attributeNames;
    private readonly byte[][] _attributeNameUtf8;
    private readonly byte[] _values;
    private readonly int[] _starts;
    private readonly int[] _lengths;

    internal Element(string[] attributeNames, byte[][] attributeNameUtf8, byte[] values, int[] starts, int[] lengths)
    {
        _attributeNames = attributeNames;
        _attributeNameUtf8 = attributeNameUtf8;
        _values = values;
        _starts = starts;
        _lengths = lengths;
    }

    public bool HasAttribute(string name) => TryGetAttribute(name, out _);

    public bool TryGetAttribute(string name, out ReadOnlySpan<byte> value)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (var index = 0; index < _attributeNames.Length; index++)
        {
            if (_attributeNames[index].Equals(name, StringComparison.Ordinal))
                return TryGetAttribute(index, out value);
        }
        value = default;
        return false;
    }

    public bool TryGetAttribute(ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)
    {
        for (var index = 0; index < _attributeNameUtf8.Length; index++)
        {
            if (name.SequenceEqual(_attributeNameUtf8[index]))
                return TryGetAttribute(index, out value);
        }
        value = default;
        return false;
    }

    private bool TryGetAttribute(int index, out ReadOnlySpan<byte> value)
    {
        var length = _lengths[index];
        if (length < 0)
        {
            value = default;
            return false;
        }
        value = _values.AsSpan(_starts[index], length);
        return true;
    }
}
