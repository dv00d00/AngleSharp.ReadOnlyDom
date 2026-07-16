namespace AngleSharp.ReadOnlyDom.Streaming;

public readonly ref struct Utf8DoctypeToken(
    ReadOnlySpan<byte> name,
    ReadOnlySpan<byte> publicIdentifier,
    bool isPublicIdentifierMissing,
    ReadOnlySpan<byte> systemIdentifier,
    bool isSystemIdentifierMissing,
    bool isQuirksForced
)
{
    public ReadOnlySpan<byte> Name { get; } = name;
    public ReadOnlySpan<byte> PublicIdentifier { get; } = publicIdentifier;
    public bool IsPublicIdentifierMissing { get; } = isPublicIdentifierMissing;
    public ReadOnlySpan<byte> SystemIdentifier { get; } = systemIdentifier;
    public bool IsSystemIdentifierMissing { get; } = isSystemIdentifierMissing;
    public bool IsQuirksForced { get; } = isQuirksForced;
}
