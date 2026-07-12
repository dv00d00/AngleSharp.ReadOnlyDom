using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Html.Model;

internal class ReadOnlyDocument : ReadOnlyHtmlElement, IConstructableDocument, IReadOnlyDocument
{
    public ReadOnlyDocument(TextSource source)
        : base(null, "#document")
    {
        Source = source;
    }

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

    IReadOnlyNode? IReadOnlyNode.Parent => _parent as IReadOnlyNode;
    IReadOnlyNodeList IReadOnlyNode.ChildNodes => (IReadOnlyNodeList)_ChildNodes;

    public bool IsLoading => false;

    // Parsing errors are deliberately not retained in the minimal profile. The parser remains permissive.
    public void TrackError(Exception _) { }

    public Task WaitForReadyAsync(CancellationToken cancelToken) => Task.CompletedTask;

    public Task FinishLoadingAsync() => Task.CompletedTask;

    // A read-only parse has no browsing lifecycle, event loop, scripting, or manifest processing.
    public void ApplyManifest() { }

    public void Clear() => ChildNodes.Clear();

    public void PerformMicrotaskCheckpoint() { }

    public void ProvideStableState() { }

    public void Dispose()
    {
        Source.Dispose();
        Builder?.Dispose();
    }
}
