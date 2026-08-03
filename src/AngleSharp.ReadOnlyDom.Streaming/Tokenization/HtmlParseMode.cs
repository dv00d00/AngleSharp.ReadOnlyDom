namespace AngleSharp.ReadOnlyDom.Streaming.Utf8Tokenizer;

/// <summary>Defines the tokenizer content model selected by an external tree constructor or test harness.</summary>
public enum HtmlParseMode : byte
{
    PCData,
    RCData,
    Plaintext,
    Rawtext,
    Script,
}
