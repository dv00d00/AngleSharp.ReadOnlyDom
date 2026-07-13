using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.CompactPrototype.Arena;

internal sealed class ArenaTemplateElement(Arena arena, int handle)
    : ArenaElement(arena, handle),
        IConstructableTemplateElement
{
    public void PopulateFragment()
    {
        Arena.PopulateTemplate(NodeHandle);
    }
}
