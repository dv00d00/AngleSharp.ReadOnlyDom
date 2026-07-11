namespace AngleSharp.ReadOnlyDom.Html;

public interface IReadOnlyDocument : IReadOnlyNode, IDisposable
{
    IReadOnlyElement Head { get; }
    IReadOnlyElement Body { get; }
}
