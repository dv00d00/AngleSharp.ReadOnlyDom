using System.Collections;
using AngleSharp.Common;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.Html.Model;

internal class ReadOnlyNamedNodeMap
    : IConstructableNamedNodeMap,
        IReadOnlyNamedNodeMap,
        IConstructableAttr,
        IReadOnlyAttr
{
    private SmallReferenceList<IConstructableAttr> _additionalAttributes;
    private StringOrMemory _name;
    private StringOrMemory _value;

    IReadOnlyAttr? IReadOnlyNamedNodeMap.this[StringOrMemory name] => this[name] as IReadOnlyAttr;

    public IConstructableAttr? this[StringOrMemory name]
    {
        get
        {
            if (Length == 0)
            {
                return null;
            }

            if (_name == name)
            {
                return this;
            }

            for (var i = 0; i < _additionalAttributes.Count; i++)
            {
                var attr = _additionalAttributes[i];
                if (attr.Name == name)
                {
                    return attr;
                }
            }

            return null;
        }
    }

    public int Length { get; private set; }

    public StringOrMemory Name => _name;

    public StringOrMemory Value
    {
        get => _value;
        set => _value = value;
    }

    public bool SameAs(IConstructableNamedNodeMap? attributes)
    {
        if (attributes is null || Length != attributes.Length)
        {
            return false;
        }

        if (Length > 0 && attributes[_name]?.Value != _value)
        {
            return false;
        }

        for (var i = 0; i < _additionalAttributes.Count; i++)
        {
            var source = _additionalAttributes[i];
            if (attributes[source.Name]?.Value != source.Value)
            {
                return false;
            }
        }

        return true;
    }

    IEnumerator<IReadOnlyAttr> IEnumerable<IReadOnlyAttr>.GetEnumerator()
    {
        if (Length == 0)
        {
            yield break;
        }

        yield return this;
        for (var i = 0; i < _additionalAttributes.Count; i++)
        {
            yield return (IReadOnlyAttr)_additionalAttributes[i];
        }
    }

    public IEnumerator<IConstructableAttr> GetEnumerator()
    {
        if (Length == 0)
        {
            yield break;
        }

        yield return this;
        for (var i = 0; i < _additionalAttributes.Count; i++)
        {
            yield return _additionalAttributes[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Add(IConstructableAttr attr)
    {
        if (Length == 0)
        {
            _name = attr.Name;
            _value = attr.Value;
        }
        else
        {
            _additionalAttributes.Add(attr);
        }

        Length++;
    }

    public void Remove(IConstructableAttr attr)
    {
        if (Length == 0)
        {
            return;
        }

        if (ReferenceEquals(attr, this))
        {
            if (_additionalAttributes.Count == 0)
            {
                _name = default;
                _value = default;
            }
            else
            {
                var replacement = _additionalAttributes[0];
                _name = replacement.Name;
                _value = replacement.Value;
                _additionalAttributes.RemoveAt(0);
            }

            Length--;
            return;
        }

        if (_additionalAttributes.Remove(attr))
        {
            Length--;
        }
    }

    public void Clear()
    {
        _name = default;
        _value = default;
        _additionalAttributes.Clear();
        Length = 0;
    }

    internal void AddOrUpdate(StringOrMemory name, StringOrMemory value)
    {
        var item = this[name];
        if (item is not null)
        {
            item.Value = value;
        }
        else if (Length == 0)
        {
            _name = name;
            _value = value;
            Length = 1;
        }
        else
        {
            _additionalAttributes.Add(new ReadOnlyAttr(name, value));
            Length++;
        }
    }

    internal ReadOnlyNamedNodeMap Clone()
    {
        var clone = new ReadOnlyNamedNodeMap();
        if (Length == 0)
        {
            return clone;
        }

        clone.AddOrUpdate(_name, _value);
        for (var i = 0; i < _additionalAttributes.Count; i++)
        {
            var attribute = _additionalAttributes[i];
            clone.AddOrUpdate(attribute.Name, attribute.Value);
        }

        return clone;
    }
}
