using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.CompactPrototype.Arena;

internal sealed class ArenaDocument : ArenaElement, IConstructableDocument, IDisposable
{
    public ArenaDocument(Arena arena, int handle, TextSource source)
        : base(arena, handle) => Source = source;

    public TextSource Source { get; }
    public IDisposable? Builder { get; set; }
    public QuirksMode QuirksMode { get; set; }
    public bool IsLoading => false;
    public IConstructableElement DocumentElement => ChildNodes.OfType<IConstructableElement>().First();
    public IConstructableElement? Head =>
        DocumentElement
            .ChildNodes.OfType<IConstructableElement>()
            .FirstOrDefault(element => element.LocalName.Equals(TagNames.Head));

    public void PerformMicrotaskCheckpoint() { }

    public void ProvideStableState() { }

    public void TrackError(Exception exception) { }

    public Task WaitForReadyAsync(CancellationToken cancelToken) => Task.CompletedTask;

    public Task FinishLoadingAsync() => Task.CompletedTask;

    public void ApplyManifest() { }

    public void Dispose()
    {
        try
        {
            Builder?.Dispose();
            Source.Dispose();
        }
        finally
        {
            Arena.Dispose();
        }
    }
}
