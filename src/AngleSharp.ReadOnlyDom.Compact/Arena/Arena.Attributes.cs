using AngleSharp.Common;
using AngleSharp.ReadOnlyDom.Compact.Parsing;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed partial class Arena
{
    public int AttributeCount(int handle)
    {
        var payload = _columns.PayloadIndexes[handle];
        return payload < 0 ? 0 : _payloads![payload].AttributeCount;
    }

    internal StringOrMemory GetAttributeValue(int handle, StringOrMemory name)
    {
        if (!_names.TryGetId(name, out var nameId))
            return default;
        var attributes = _attributes;
        if (attributes is null)
            return default;
        for (var attribute = FirstAttribute(handle); attribute >= 0; attribute = attributes[attribute].Next)
            if (attributes[attribute].NameId == nameId)
                return attributes[attribute].Value;
        return default;
    }

    internal bool HasAttribute(int handle, StringOrMemory name)
    {
        if (!_names.TryGetId(name, out var nameId) || _attributes is not { } attributes)
            return false;
        for (var attribute = FirstAttribute(handle); attribute >= 0; attribute = attributes[attribute].Next)
            if (attributes[attribute].NameId == nameId)
                return true;
        return false;
    }

    public StringOrMemory AttributeName(int handle)
    {
        return _names.GetName(_attributes![handle].NameId);
    }

    public StringOrMemory AttributeValue(int handle)
    {
        return _attributes![handle].Value;
    }

    internal int FirstAttributeHandle(int handle)
    {
        return FirstAttribute(handle);
    }

    internal int NextAttribute(int attribute)
    {
        return _attributes![attribute].Next;
    }

    internal bool AttributesSame(int left, int right)
    {
        if (AttributeCount(left) != AttributeCount(right))
            return false;
        var attributes = _attributes;
        if (attributes is null)
            return true;
        for (var attribute = FirstAttribute(left); attribute >= 0; attribute = attributes[attribute].Next)
        {
            var candidate = FirstAttribute(right);
            while (
                candidate >= 0
                && (
                    attributes[candidate].NameId != attributes[attribute].NameId
                    || attributes[candidate].Value != attributes[attribute].Value
                )
            )
                candidate = attributes[candidate].Next;
            if (candidate < 0)
                return false;
        }

        return true;
    }

    public void SetAttributeValue(int handle, StringOrMemory value)
    {
        ref var attribute = ref _attributes![handle];
        _textLength = checked(_textLength - attribute.Value.Length + value.Length);
        attribute.Value = value;
    }

    public void SetOwnAttribute(int handle, StringOrMemory name, StringOrMemory value)
    {
        SetOwnAttribute(handle, _names.GetId(name), value);
    }

    private void SetOwnAttribute(int handle, ushort nameId, StringOrMemory value)
    {
        for (var existing = FirstAttribute(handle); existing >= 0; existing = _attributes![existing].Next)
            if (_attributes![existing].NameId == nameId)
            {
                SetAttributeValue(existing, value);
                return;
            }

        _attributes ??= new PooledValueBuffer<MutableAttribute>(
            ValidateCapacity(_hints.InitialAttributeCapacity, nameof(CompactParserHints.InitialAttributeCapacity))
        );
        var payloadIndex = EnsurePayload(handle);
        ref var payload = ref _payloads![payloadIndex];
        var attributeHandle = _attributes.Add(new MutableAttribute(nameId, value));
        _textLength = checked(_textLength + value.Length);
        if (payload.FirstAttribute < 0)
            payload.FirstAttribute = attributeHandle;
        else
            _attributes[payload.LastAttribute].Next = attributeHandle;
        payload.LastAttribute = attributeHandle;
        payload.AttributeCount++;
        _constructionView?.AttributeRetained(value);
    }

    public void CompleteAttributes(int handle)
    {
        _constructionView?.CompleteAttributes(this, handle);
    }

    public void CopyAttributes(int source, int destination)
    {
        var attributes = _attributes;
        if (attributes is null)
            return;
        for (var attribute = FirstAttribute(source); attribute >= 0; attribute = attributes[attribute].Next)
            SetOwnAttribute(destination, attributes[attribute].NameId, attributes[attribute].Value);
    }

    private void SetValue(int handle, StringOrMemory value)
    {
        if (value.Length != 0)
        {
            var payload = EnsurePayload(handle);
            _textLength = checked(_textLength - _payloads![payload].Value.Length + value.Length);
            _payloads![payload].Value = value;
        }
    }

    private int FirstAttribute(int handle)
    {
        var payload = _columns.PayloadIndexes[handle];
        return payload < 0 ? -1 : _payloads![payload].FirstAttribute;
    }

    private int EnsurePayload(int handle)
    {
        var payload = _columns.PayloadIndexes[handle];
        if (payload >= 0)
            return payload;
        _payloads ??= new PooledValueBuffer<MutableNodePayload>(
            ValidateCapacity(_hints.InitialPayloadCapacity, nameof(CompactParserHints.InitialPayloadCapacity))
        );
        payload = _payloads.Add(new MutableNodePayload());
        _columns.PayloadIndexes[handle] = payload;
        return payload;
    }
}