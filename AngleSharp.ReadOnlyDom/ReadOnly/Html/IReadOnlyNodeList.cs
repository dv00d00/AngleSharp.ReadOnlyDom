namespace AngleSharp.ReadOnlyDom.ReadOnly.Html;

public interface IReadOnlyNodeList : IEnumerable<IReadOnlyNode>
{
    IReadOnlyNode this[Int32 index] { get; }
    Int32 Length { get; }
}