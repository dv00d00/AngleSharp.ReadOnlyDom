using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

/// <summary>
/// The construction-time document handed to AngleSharp's tree builder. Topology is reached through
/// <see cref="ArenaHandle"/> and the factory's node accessors, so this carries document state only.
/// </summary>
internal sealed class ArenaDocument : IConstructableDocumentState, IDisposable
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
    {
        Arena = arena;
        NodeHandle = handle;
        Source = source;
        _options = options;
        _layout = layout;
    }

    internal Arena Arena { get; }
    internal int NodeHandle { get; }

    public TextSource Source { get; }
    public IDisposable? Builder { get; set; }
    public QuirksMode QuirksMode { get; set; }

    public void AddComment(ref StructHtmlToken token) => Arena.AddComment(NodeHandle, ref token);

    public void TrackError(Exception exception) { }

    public CompactDocument CreateCompactDocument()
    {
        if (_layout == CompactDocumentLayout.Packed || !Arena.CanFreeze(NodeHandle))
            return Arena.Finalize(NodeHandle, _options);

        var result = Arena.Freeze(NodeHandle, _options, Source);
        _ownershipTransferred = true;
        return result;
    }

    public CompactAggregateResult CreateAggregateResult(int inputBytesConsumed) =>
        Arena.CreateAggregateResult(NodeHandle, inputBytesConsumed);

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
