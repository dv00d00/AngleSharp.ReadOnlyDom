using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed class ArenaDocument : ArenaElement, IConstructableDocument, IDisposable
{
    private bool _ownershipTransferred;
    private readonly CompactMetadataOptions _options;
    private readonly CompactDocumentLayout _layout;

    public ArenaDocument(
        Arena arena,
        int handle,
        TextSource source,
        CompactMetadataOptions options,
        CompactDocumentLayout layout
    )
        : base(arena, handle)
    {
        Source = source;
        _options = options;
        _layout = layout;
    }

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

    public CompactDocument CreateCompactDocument()
    {
        if (_layout == CompactDocumentLayout.Packed || !Arena.CanFreeze(NodeHandle))
            return Arena.Finalize(NodeHandle, _options);

        var result = Arena.Freeze(NodeHandle, _options, Source);
        _ownershipTransferred = true;
        return result;
    }

    public CompactStreamingExtractionResult CreateStreamingExtractionResult(int inputBytesConsumed) =>
        Arena.CreateStreamingExtractionResult(NodeHandle, inputBytesConsumed);

    public void SetTokensProcessed(int count) => Arena.SetTokensProcessed(count);

    public void Dispose()
    {
        try
        {
            Builder?.Dispose();
            if (!_ownershipTransferred)
                Source.Dispose();
        }
        finally
        {
            if (!_ownershipTransferred)
                Arena.Dispose();
        }
    }
}
