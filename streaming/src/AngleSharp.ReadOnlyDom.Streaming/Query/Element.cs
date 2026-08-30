namespace AngleSharp.ReadOnlyDom.Streaming.Query;

/// <summary>
/// Provides attribute values for a callback-scoped <see cref="Element"/> view. Values are decoded
/// lazily on first read: the execution stores raw captured bytes and runs the character-reference
/// decoder only for attributes something actually observes.
/// </summary>
internal interface IElementAttributeSource
{
    bool TryGetAttributeValue(int index, out ReadOnlySpan<byte> value);
}

/// <summary>
/// A callback-scoped start-tag view. Attribute spans borrow execution buffers and are valid only until
/// the callback returns; only attributes projected by that query node are visible.
/// </summary>
public readonly ref struct Element
{
    private readonly string[] _attributeNames;
    private readonly byte[][] _attributeNameUtf8;
    private readonly IElementAttributeSource _source;
    private readonly ulong _allowedAttributeMask;

    internal Element(
        string[] attributeNames,
        byte[][] attributeNameUtf8,
        IElementAttributeSource source,
        ulong allowedAttributeMask
    )
    {
        _attributeNames = attributeNames;
        _attributeNameUtf8 = attributeNameUtf8;
        _source = source;
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
                return _source.TryGetAttributeValue(index, out value);
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
                return _source.TryGetAttributeValue(index, out value);
        }
        value = default;
        return false;
    }
}
