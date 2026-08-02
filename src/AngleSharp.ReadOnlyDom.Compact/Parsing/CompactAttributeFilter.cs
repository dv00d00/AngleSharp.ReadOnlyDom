using AngleSharp.Html.Parser.Tokens.Struct;

namespace AngleSharp.ReadOnlyDom.Compact.Parsing;

public delegate bool CompactAttributeFilter(ref StructHtmlToken token, ReadOnlyMemory<char> attributeName);