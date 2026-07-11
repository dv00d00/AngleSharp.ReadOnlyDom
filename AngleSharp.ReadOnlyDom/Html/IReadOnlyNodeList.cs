namespace AngleSharp.ReadOnlyDom.Html;

public interface IReadOnlyNodeList : IEnumerable<IReadOnlyNode>
{
    IReadOnlyNode this[int index] { get; }
    int Length { get; }
}