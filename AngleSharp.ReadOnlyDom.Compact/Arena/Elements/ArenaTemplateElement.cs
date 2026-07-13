using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed class ArenaTemplateElement(Arena arena, int handle)
    : ArenaElement(arena, handle),
        IConstructableTemplateElement
{
    public void PopulateFragment()
    {
        Arena.PopulateTemplate(NodeHandle);
    }
}
