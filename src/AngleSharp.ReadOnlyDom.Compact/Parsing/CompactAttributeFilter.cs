using AngleSharp.Html.Parser.Tokens.Struct;

namespace AngleSharp.ReadOnlyDom.Compact.Parsing;

internal delegate bool CompactAttributeFilter(ref StructHtmlToken token, ReadOnlyMemory<char> attributeName);
