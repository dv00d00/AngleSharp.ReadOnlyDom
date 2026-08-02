# AngleSharp generic construction notes

## Handle-oriented tree construction

FsCheck differential testing against AngleSharp 1.5.2 reduced a mismatch to malformed table content such as:

```html
<table><div><span attr='unterminated>
```

The standard mutable DOM foster-parents `div` into `body` before `table`. A DOM built through
`IDomConstructionElementFactory<TDocument, TElement>` places it outside `html`.

The original `HtmlDomBuilder<TDocument, TElement>.AddElementWithFoster` checked `el is HtmlTemplateElement` and
`el is HtmlTableElement`. Those AngleSharp core concrete types prevented a custom constructable element from matching
mutable parser behavior.

The handle-oriented `HtmlTreeBuilder<TDocument, TNode>` now uses parser flags and HTML tag identity instead. Compact parsing
therefore handles foster parenting, templates, and formatting adoption directly through `ArenaHandle`, without a parallel
object tree. Differential and smoke coverage retains the malformed-table case above as a regression contract.

## UTF-8 token-source adapter

The streaming product now owns its native UTF-8 tokenizer, WHATWG entity table, encoding-label table, limits, and sink
contract, and therefore has no runtime dependency on AngleSharp. The mutable-DOM token-source adapter remains core
specific; object and compact construction continue to consume the fork until their required construction APIs ship.
