namespace AngleSharp.ReadOnlyDom.Html;

public interface IReadOnlyDocument : IReadOnlyNode, IDisposable
{
    IReadOnlyElement DocumentElement { get; }
    IReadOnlyElement Head { get; }
    IReadOnlyElement Body { get; }
}
