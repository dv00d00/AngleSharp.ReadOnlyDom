using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html.Model;

internal class ReadOnlyDocument : ReadOnlyHtmlElement, IConstructableDocument, IReadOnlyDocument, IConstructableElement
{
    public ReadOnlyDocument(TextSource source) : base(null, "#document")
    {
        Source = source;
    }

    public TextSource Source { get; set; }
    public IDisposable? Builder { get; set; }
    public QuirksMode QuirksMode { get; set; }

    public IConstructableElement DocumentElement => this;
    public IConstructableElement Head => _ChildNodes.OfType<IConstructableElement>().First(n => n.LocalName == "head");
    IReadOnlyElement IReadOnlyDocument.Body => _ChildNodes.OfType<IReadOnlyElement>().First(n => n.LocalName == "body");
    IReadOnlyElement IReadOnlyDocument.Head => _ChildNodes.OfType<IReadOnlyElement>().First(n => n.LocalName == "head");
    IReadOnlyNode? IReadOnlyNode.Parent => _parent as IReadOnlyNode;
    IReadOnlyNodeList IReadOnlyNode.ChildNodes => _ChildNodes;

    public bool IsLoading => false;

    public void TrackError(Exception exception) { }

    public Task WaitForReadyAsync(CancellationToken cancelToken) => Task.CompletedTask;

    public Task FinishLoadingAsync() => Task.CompletedTask;

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