using AngleSharp.Common;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html.Model;

internal class ReadOnlyAttr : IReadOnlyAttr, IConstructableAttr
{
    public StringOrMemory Name { get; }
    public StringOrMemory Value { get; set; }

    public ReadOnlyAttr(StringOrMemory name, StringOrMemory value)
    {
        Name = name;
        Value = value;
    }
}