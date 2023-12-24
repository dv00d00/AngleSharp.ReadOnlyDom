namespace AngleSharp.ReadOnlyDom.ReadOnly.Html;

public interface IReadOnlyDocument : IReadOnlyNode, IDisposable
{
    IReadOnlyElement Head { get; }
    IReadOnlyElement Body { get; }
}