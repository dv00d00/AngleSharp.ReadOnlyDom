namespace AngleSharp.ReadOnlyDom.ReadOnly.Html;

public interface IReadOnlyNodeList : IEnumerable<IReadOnlyNode>
{
    IReadOnlyNode this[int index] { get; }
    int Length { get; }
}