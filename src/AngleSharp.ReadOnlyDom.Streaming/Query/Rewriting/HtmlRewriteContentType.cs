namespace AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;

/// <summary>Controls whether inserted UTF-8 is emitted verbatim or escaped as HTML text.</summary>
public enum HtmlRewriteContentType : byte
{
    /// <summary>The supplied bytes are trusted markup and are emitted verbatim.</summary>
    Html,

    /// <summary>The supplied bytes are text; ampersands and angle brackets are escaped.</summary>
    Text,
}
