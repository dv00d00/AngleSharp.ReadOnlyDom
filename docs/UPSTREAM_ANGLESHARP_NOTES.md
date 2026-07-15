# AngleSharp generic construction gaps

## Foster parenting uses core DOM concrete types

FsCheck differential testing against AngleSharp 1.5.2 reduced a mismatch to malformed table content such as:

```html
<table><div><span attr='unterminated>
```

The standard mutable DOM foster-parents `div` into `body` before `table`. A DOM built through
`IDomConstructionElementFactory<TDocument, TElement>` places it outside `html`.

`HtmlDomBuilder<TDocument, TElement>.AddElementWithFoster` checks `el is HtmlTemplateElement` and
`el is HtmlTableElement`. Those are AngleSharp core concrete types, so a custom constructable element can never satisfy
the checks. The generic path should use tag identity and flags, or construction capabilities, instead of core DOM types.

This is relevant to the opaque-handle construction sink considered by issue #6: a direct indexed DOM cannot match the
mutable parser on foster-parenting cases until the generic builder removes these concrete-type assumptions.

## Experimental token-source adapter

`src/AngleSharp.ReadOnlyDom.Streaming.AngleSharp` and its matching project under `tests/` target the proposed
`IHtmlTokenSource` integration seam. The current local AngleSharp checkout does not expose that interface, so these two
projects intentionally remain outside `AngleSharp.ReadOnlyDom.slnx` until the upstream contract is available again or
the adapter is retired.
