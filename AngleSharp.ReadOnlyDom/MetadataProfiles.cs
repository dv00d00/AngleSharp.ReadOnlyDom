using AngleSharp.Html.Parser;

namespace AngleSharp.ReadOnlyDom;

public enum ReadOnlyMetadataProfile
{
    Minimal,
    Navigable,
    SourceMapped,
    Diagnostic,
}

public enum SourceFidelity
{
    Offsets,
    Positions,
    Tokens,
}

[Flags]
internal enum MetadataFeatures
{
    None = 0,
    ParentLinks = 1 << 0,
    QualifiedNames = 1 << 1,
    SourceReferences = 1 << 2,
    Diagnostics = 1 << 3,
    Comments = 1 << 4,
    ProcessingInstructions = 1 << 5,
}

internal static class MetadataProfileContract
{
    public static MetadataFeatures Features(this ReadOnlyMetadataProfile profile) =>
        profile switch
        {
            ReadOnlyMetadataProfile.Minimal => MetadataFeatures.ParentLinks,
            ReadOnlyMetadataProfile.Navigable => MetadataFeatures.ParentLinks
                | MetadataFeatures.QualifiedNames
                | MetadataFeatures.Comments,
            ReadOnlyMetadataProfile.SourceMapped => MetadataFeatures.ParentLinks
                | MetadataFeatures.QualifiedNames
                | MetadataFeatures.SourceReferences,
            ReadOnlyMetadataProfile.Diagnostic => MetadataFeatures.ParentLinks
                | MetadataFeatures.QualifiedNames
                | MetadataFeatures.SourceReferences
                | MetadataFeatures.Diagnostics
                | MetadataFeatures.Comments
                | MetadataFeatures.ProcessingInstructions,
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };

    public static SourceFidelity? Fidelity(this ReadOnlyMetadataProfile profile) =>
        profile switch
        {
            ReadOnlyMetadataProfile.SourceMapped => SourceFidelity.Positions,
            ReadOnlyMetadataProfile.Diagnostic => SourceFidelity.Positions,
            _ => null,
        };

    public static HtmlParserOptions ParserOptions(this ReadOnlyMetadataProfile profile)
    {
        var features = profile.Features();
        return new HtmlParserOptions
        {
            IsKeepingSourceReferences = features.HasFlag(MetadataFeatures.SourceReferences),
            IsSupportingProcessingInstructions = features.HasFlag(MetadataFeatures.ProcessingInstructions),
            SkipComments = !features.HasFlag(MetadataFeatures.Comments),
            SkipProcessingInstructions = !features.HasFlag(MetadataFeatures.ProcessingInstructions),
        };
    }
}
