using AngleSharp.Html.Parser.Tokens.Struct;

namespace AngleSharp.ReadOnlyDom.Compact;

public delegate bool CompactAttributeFilter(ref StructHtmlToken token, ReadOnlyMemory<char> attributeName);
