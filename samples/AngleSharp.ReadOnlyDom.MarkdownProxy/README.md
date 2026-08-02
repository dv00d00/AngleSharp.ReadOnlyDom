# Streaming HTML-to-Markdown sample

This sample demonstrates one focused use case: a caller-provided UTF-8 HTML body is folded into Markdown by a compiled
streaming query plan.

```powershell
dotnet run --project samples/AngleSharp.ReadOnlyDom.MarkdownProxy -c Release

# Open the local UI, or POST HTML directly:
curl.exe -X POST http://localhost:5000/markdown `
  -H "Content-Type: text/html; charset=utf-8" `
  --data-binary "<html><body><article><h1>Hello</h1><p>From <b>RODOM</b>.</p></article></body></html>"

# The buffered terminal is available only to contrast execution shapes:
curl.exe -X POST "http://localhost:5000/markdown?stream=false" `
  -H "Content-Type: text/html; charset=utf-8" `
  --data-binary "<html><body><article><p>Small buffered result.</p></article></body></html>"
```

The default endpoint uses the backpressured lane. Synchronous tokenizer/query work marks final UTF-8 prefixes safe to
publish, and the outer async pump flushes them to `HttpResponse.BodyWriter` before consuming more bounded input slices.
The sample caps input at 4 MiB through both an early `Content-Length` check and `HtmlStreamingLimits`, so chunked bodies
are bounded as well.

The project intentionally contains no remote URL fetching, redirects, asset proxy, browser Markdown renderer, or
plain-text converter. Those are application policies, security boundaries, or separate examples—not part of the
streaming query demonstration. The UI displays generated Markdown as text and never injects it as HTML.

`MarkdownPlan` remains deliberately small and incomplete. It recognizes a limited set of explicit tags and observes the
streaming engine's lexical start/end-tag topology rather than browser-corrected HTML tree topology. Omitted end tags,
foster parenting, formatting adoption, and self-closing syntax on ordinary HTML elements can therefore differ from a DOM
converter. Use the retained DOM lanes when corrected tree semantics are required.

The plan's "prefer the first article, otherwise use the whole page" heuristic demonstrates a publication frontier.
Before an `article` appears, broad-page output remains tentative because it may be discarded. Once the article is
selected, completed Markdown blocks can be published. A page with no article necessarily retains its fallback until EOF.
