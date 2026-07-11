using AngleSharp.Common;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.Html.Model;

internal class ReadOnlyAttr(StringOrMemory name, StringOrMemory value) : IReadOnlyAttr, IConstructableAttr
{
    public StringOrMemory Name { get; } = name;
    public StringOrMemory Value { get; set; } = value;
}