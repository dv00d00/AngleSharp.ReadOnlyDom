namespace AngleSharp.ReadOnlyDom.Streaming.Public;

/// <summary>
/// A callback-scoped start-tag view. Attribute spans borrow execution buffers and are valid only until
/// the callback returns; only attributes projected by that query node are visible.
/// </summary>
public readonly ref struct Element
{
    private readonly string[] _attributeNames;
    private readonly byte[][] _attributeNameUtf8;
    private readonly byte[] _values;
    private readonly int[] _starts;
    private readonly int[] _lengths;
    private readonly ulong _allowedAttributeMask;

    internal Element(
        string[] attributeNames,
        byte[][] attributeNameUtf8,
        byte[] values,
        int[] starts,
        int[] lengths,
        ulong allowedAttributeMask
    )
    {
        _attributeNames = attributeNames;
        _attributeNameUtf8 = attributeNameUtf8;
        _values = values;
        _starts = starts;
        _lengths = lengths;
        _allowedAttributeMask = allowedAttributeMask;
    }

    /// <summary>Checks a projected HTML attribute name using ASCII-compatible case-insensitive matching.</summary>
    public bool HasAttribute(string name) => TryGetAttribute(name, out _);

    /// <summary>Returns a callback-scoped borrowed value for a projected attribute.</summary>
    public bool TryGetAttribute(string name, out ReadOnlySpan<byte> value)
    {
        ArgumentNullException.ThrowIfNull(name);
        var attributes = _allowedAttributeMask;
        while (attributes != 0)
        {
            var index = System.Numerics.BitOperations.TrailingZeroCount(attributes);
            attributes &= attributes - 1;
            if (_attributeNames[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                return TryGetAttribute(index, out value);
        }
        value = default;
        return false;
    }

    /// <summary>
    /// Returns a callback-scoped borrowed value. The UTF-8 name must use the normalized lowercase spelling
    /// compiled by the query.
    /// </summary>
    public bool TryGetAttribute(ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)
    {
        var attributes = _allowedAttributeMask;
        while (attributes != 0)
        {
            var index = System.Numerics.BitOperations.TrailingZeroCount(attributes);
            attributes &= attributes - 1;
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
