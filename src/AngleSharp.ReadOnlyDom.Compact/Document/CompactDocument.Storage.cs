namespace AngleSharp.ReadOnlyDom.Compact.Document;

public sealed partial class CompactDocument
{
    internal CompactNode GetNode(int handle)
    {
        if (_arena is null)
            return _nodes![handle];
        return new CompactNode(
            _arena.FrozenFirstChild(handle),
            _arena.FrozenSubtreeEnd(handle),
            _arena.FrozenPayloadIndex(handle),
            _arena.FrozenNameId(handle),
            _arena.FrozenKind(handle),
            _arena.FrozenFlags(handle)
        );
    }

    internal CompactNodeKind KindAt(int handle)
    {
        return _arena is null ? _nodes![handle].Kind : _arena.FrozenKind(handle);
    }

    internal ushort NameIdAt(int handle)
    {
        return _arena is null ? _nodes![handle].NameId : _arena.FrozenNameId(handle);
    }

    internal int PayloadIndexAt(int handle)
    {
        return _arena is null ? _nodes![handle].PayloadIndex : _arena.FrozenPayloadIndex(handle);
    }

    internal int SubtreeEndAt(int handle)
    {
        return _arena is null ? _nodes![handle].SubtreeEndExclusive : _arena.FrozenSubtreeEnd(handle);
    }

    internal CompactNodePayload GetPayload(int index)
    {
        if (_arena is null)
            return _payloads![index];
        var value = _arena.FrozenPayloadValue(index);
        return new CompactNodePayload(
            _arena.FrozenFirstAttribute(index),
            value.IsEmpty ? -1 : EncodePayloadValue(index),
            value.Length,
            _arena.FrozenAttributeCount(index)
        );
    }

    internal CompactAttribute GetAttribute(int index)
    {
        if (_arena is null)
            return _attributes![index];
        var value = _arena.FrozenAttributeValue(index);
        return new CompactAttribute(
            _arena.FrozenAttributeNameId(index),
            value.IsEmpty ? -1 : EncodeAttributeValue(index),
            value.Length
        );
    }

    // Lightweight column accessors used by the hot attribute-lookup loops. They avoid materializing a
    // CompactNode/CompactNodePayload/CompactAttribute struct (and, on the frozen path, the encode/decode
    // round-trip) when the caller only needs the name ID or the value span.
    internal int PayloadFirstAttributeAt(int payloadIndex)
    {
        return _arena is null ? _payloads![payloadIndex].FirstAttribute : _arena.FrozenFirstAttribute(payloadIndex);
    }

    internal int PayloadAttributeCountAt(int payloadIndex)
    {
        return _arena is null ? _payloads![payloadIndex].AttributeCount : _arena.FrozenAttributeCount(payloadIndex);
    }

    internal ushort AttributeNameIdAt(int index)
    {
        return _arena is null ? _attributes![index].NameId : _arena.FrozenAttributeNameId(index);
    }

    internal ReadOnlySpan<char> AttributeValueSpanAt(int index)
    {
        if (_arena is null)
        {
            ref readonly var attribute = ref _attributes![index];
            return attribute.ValueLength == 0 ? [] : _text!.AsSpan(attribute.ValueStart, attribute.ValueLength);
        }

        return _arena.FrozenAttributeValue(index).Span;
    }

    /// <summary>The value span for a payload index, without materializing a <see cref="CompactNodePayload" />.</summary>
    internal ReadOnlySpan<char> PayloadValueSpanAt(int payloadIndex)
    {
        if (_arena is null)
        {
            ref readonly var payload = ref _payloads![payloadIndex];
            return payload.ValueLength == 0 ? [] : _text!.AsSpan(payload.ValueStart, payload.ValueLength);
        }

        return _arena.FrozenPayloadValue(payloadIndex).Span;
    }

    /// <summary>Returns the first-attribute index and count for a node handle, or false when it has no payload.</summary>
    internal bool TryGetAttributeRange(int handle, out int firstAttribute, out int count)
    {
        var payloadIndex = PayloadIndexAt(handle);
        if (payloadIndex < 0)
        {
            firstAttribute = 0;
            count = 0;
            return false;
        }

        firstAttribute = PayloadFirstAttributeAt(payloadIndex);
        count = PayloadAttributeCountAt(payloadIndex);
        return true;
    }

    internal bool TryGetAttribute(int handle, ushort nameId, out CompactAttribute attribute, ref int inspected)
    {
        if (nameId != ushort.MaxValue && TryGetAttributeRange(handle, out var first, out var count))
            for (var index = first; index < first + count; index++)
            {
                inspected++;
                if (AttributeNameIdAt(index) == nameId)
                {
                    attribute = GetAttribute(index);
                    return true;
                }
            }

        attribute = default;
        return false;
    }

    internal ReadOnlySpan<char> GetValue(int start, int length)
    {
        if (length == 0)
            return [];
        if (_arena is null)
            return _text!.AsSpan(start, length);
        var memory = IsAttributeValue(start)
            ? _arena.FrozenAttributeValue(DecodeValueIndex(start))
            : _arena.FrozenPayloadValue(DecodeValueIndex(start));
        return memory.Span[..length];
    }

    internal int GetParent(int handle)
    {
        if (!RetainsParentLinks)
            throw new InvalidOperationException("Parent links were not retained.");
        return _arena is null ? _parents![handle] : _arena.FrozenParent(handle);
    }

    internal bool TryGetSourceLocation(int handle, out CompactSourceLocation source)
    {
        if (_arena is not null)
        {
            if (RetainsSourceLocations)
                return _arena.TryGetFrozenSourceLocation(handle, out source);
            source = default;
            return false;
        }

        if (_sources is not null && _sources[handle].Index >= 0)
        {
            source = _sources[handle];
            return true;
        }

        source = default;
        return false;
    }

    private static int EncodePayloadValue(int index)
    {
        return checked(index << 1);
    }

    private static int EncodeAttributeValue(int index)
    {
        return checked((index << 1) | 1);
    }

    private static bool IsAttributeValue(int value)
    {
        return (value & 1) != 0;
    }

    private static int DecodeValueIndex(int value)
    {
        return value >> 1;
    }
}
