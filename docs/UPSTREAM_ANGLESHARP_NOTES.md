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

The UTF-8 tokenizer and its mutable-DOM token-source adapter now live in AngleSharp core. RODOM consumes the tokenizer
sink contract directly and no longer carries a second `IHtmlTokenSource` implementation.
