using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Html.Model;

internal class ReadOnlyDocument
    : ReadOnlyHtmlElement,
        IConstructableDocumentNode,
        IReadOnlyDocument,
        IReadOnlyDiagnostics,
        IReadOnlySourceMetadata
{
    private readonly List<Exception>? _errors;
    private readonly Dictionary<IReadOnlyElement, ISourceReference?>? _sourceReferences;

    public ReadOnlyDocument(TextSource source, ReadOnlyMetadataProfile profile = ReadOnlyMetadataProfile.Minimal)
        : base(null, "#document")
    {
        Source = source;
        MetadataProfile = profile;
        var features = profile.Features();
        if (features.HasFlag(MetadataFeatures.Diagnostics))
            _errors = [];
        if (features.HasFlag(MetadataFeatures.SourceReferences))
            _sourceReferences = [];
    }

    public ReadOnlyMetadataProfile MetadataProfile { get; }

    public TextSource Source { get; set; }
    public IDisposable? Builder { get; set; }
    public QuirksMode QuirksMode { get; set; }

    public IConstructableElement DocumentElement => FindDocumentElement();
    public IConstructableElement? Head => FindHtmlChild("head");

    IReadOnlyElement IReadOnlyDocument.DocumentElement => FindDocumentElement();
    IReadOnlyElement IReadOnlyDocument.Body => FindHtmlChild("body") ?? throw MissingDocumentElement("body");
    IReadOnlyElement IReadOnlyDocument.Head => FindHtmlChild("head") ?? throw MissingDocumentElement("head");

    private ReadOnlyHtmlElement FindDocumentElement()
    {
        foreach (var node in _ChildNodes)
        {
            if (node is ReadOnlyHtmlElement element)
            {
                return element;
            }
        }

        throw MissingDocumentElement("html");
    }

    private ReadOnlyHtmlElement? FindHtmlChild(StringOrMemory localName)
    {
        foreach (var node in FindDocumentElement().ChildNodes)
        {
            if (node is ReadOnlyHtmlElement element && element.LocalName == localName)
            {
                return element;
            }
        }

        return null;
    }

    private static InvalidOperationException MissingDocumentElement(StringOrMemory localName) =>
        new($"No document element with local name '{localName}' was found.");

    public void TrackError(Exception error) => _errors?.Add(error);

    public bool TryGetDiagnostics(out IReadOnlyDiagnostics diagnostics)
    {
        diagnostics = this;
        return _errors is not null;
    }

    public bool TryGetSourceMetadata(out IReadOnlySourceMetadata metadata)
    {
        metadata = this;
        return _sourceReferences is not null;
    }

    IReadOnlyList<Exception> IReadOnlyDiagnostics.Errors => _errors!;
    SourceFidelity IReadOnlySourceMetadata.Fidelity => MetadataProfile.Fidelity()!.Value;

    bool IReadOnlySourceMetadata.TryGetSourceReference(
        IReadOnlyElement element,
        out ISourceReference? sourceReference
    ) => _sourceReferences!.TryGetValue(element, out sourceReference) && sourceReference is not null;

    internal bool TracksSources => _sourceReferences is not null;

    internal ISourceReference? GetSourceReference(IReadOnlyElement element) =>
        _sourceReferences is not null && _sourceReferences.TryGetValue(element, out var value) ? value : null;

    internal void SetSourceReference(IReadOnlyElement element, ISourceReference? value)
    {
        if (_sourceReferences is null)
            return;
        if (value is null)
            _sourceReferences.Remove(element);
        else
            _sourceReferences[element] = value;
    }

    public void Clear() => ChildNodes.Clear();

    public void Dispose()
    {
        Source.Dispose();
        Builder?.Dispose();
    }
}
