namespace AngleSharp.ReadOnlyDom.Html;

public interface IReadOnlyDocument : IReadOnlyNode, IDisposable
{
    ReadOnlyMetadataProfile MetadataProfile { get; }
    IReadOnlyElement DocumentElement { get; }
    IReadOnlyElement Head { get; }
    IReadOnlyElement Body { get; }
    bool TryGetDiagnostics(out IReadOnlyDiagnostics diagnostics);
    bool TryGetSourceMetadata(out IReadOnlySourceMetadata metadata);
}

public interface IReadOnlyDiagnostics
{
    IReadOnlyList<Exception> Errors { get; }
}

public interface IReadOnlySourceMetadata
{
    SourceFidelity Fidelity { get; }
    bool TryGetSourceReference(IReadOnlyElement element, out AngleSharp.Dom.ISourceReference? sourceReference);
}
