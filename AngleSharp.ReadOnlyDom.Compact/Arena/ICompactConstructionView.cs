using AngleSharp.Common;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal interface ICompactConstructionViewDefinition
{
    ICompactConstructionViewState CreateState(TextSource source);
}

internal interface ICompactConstructionViewState
{
    void SetTokensProcessed(int count);
    void NodeMaterialized();
    void AttributeRetained(StringOrMemory value);
    void CompleteAttributes(Arena arena, int handle);
    StringOrMemory SelectTextValue(StringOrMemory value);
}
