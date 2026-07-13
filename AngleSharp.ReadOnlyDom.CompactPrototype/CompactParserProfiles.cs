using AngleSharp.Html.Parser;

namespace AngleSharp.ReadOnlyDom.CompactPrototype;

public static class CompactParserProfiles
{
    public static HtmlParserOptions Extraction =>
        new()
        {
            IsScripting = false,
            IsNotSupportingFrames = true,
            IsKeepingSourceReferences = false,
            IsPreservingAttributeNames = false,
            DisableElementPositionTracking = true,
            SkipComments = true,
            SkipProcessingInstructions = true,
            SkipCDATA = true,
            SkipPlaintext = true,
            SkipScriptText = true,
            SkipRawText = true,
        };
}
