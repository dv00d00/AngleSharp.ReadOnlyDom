# Streaming HTML-to-Markdown navigation

This sample makes the Streaming library the navigation engine. Every page transition opens a checked-in HTML file as a
`PipeReader`, runs the shared sample `QueryPlan<MarkdownBuffer>` with backpressured output, renders the returned Markdown
without `innerHTML`, and follows links by streaming the next HTML page through the same plan.

```powershell
dotnet run --project samples/AngleSharp.ReadOnlyDom.MarkdownNavigation -c Release
```

The endpoint accepts only three exact `/pages/*.html` identifiers backed by checked-in fixtures. There is no URL fetcher,
redirect handling, arbitrary filesystem path, or asset proxy. The browser renderer creates DOM nodes and assigns content
through `textContent`; local HTML-page links participate in the sample's back stack, headings support fragments, and
ordinary HTTP links open outside the navigator.

`MarkdownPlan`, `MarkdownBuffer`, and their query extensions are linked from the MarkdownProxy sample so both runnable
projects exercise one source implementation. This remains an intentionally incomplete HTML-to-Markdown and Markdown
rendering example, not a production converter.
