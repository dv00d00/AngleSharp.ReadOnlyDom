# AngleSharp.ReadOnlyDom architecture review

## Context

This project is a parallel, read-only DOM implementation built on AngleSharp's generic HTML construction pipeline. Its
primary purpose is to reduce allocations for scraping workloads while preserving the useful parts of DOM structure.

The review covered every source and project file under `AngleSharp.ReadOnlyDom/` and the AngleSharp 1.5.2
`IConstructable*` contracts and generic tree builder.

## Main conclusion

The existing AngleSharp abstraction can inject a smaller object hierarchy, but it cannot produce a genuinely compact
arena DOM. The construction interfaces require reference-type nodes with identity, parents, mutable children,
attributes, and shallow-copy operations.

There are therefore four useful optimization levels:

1. Make the current object graph correct and explicitly configurable.
2. Shrink its nodes and child/attribute collections.
3. Add a text projection path that does not construct a DOM at all.
4. For a major further reduction, introduce an index/sink-based construction API upstream or compact the graph after
   parsing.

`Node<TPayload>` should not be the starting point. It adds payload storage to every node, spreads generics through the
hierarchy, and does not naturally represent optional capabilities.

## Correctness and contract issues

The following shortcuts currently resemble supported information but silently return incomplete or incorrect values:

- `SourceReference` ignores writes even when AngleSharp's `IsKeepingSourceReferences` is enabled.
- `NamespaceUri` always returns empty, including for SVG and MathML.
- `Prefix` always returns empty even though prefixes are accepted during construction.
- `Owner` always returns `null`.
- `ReadOnlyDocument.Head` and `Body` search direct document children, although they normally live under `html`.
- `ReadOnlyMathElement.ShallowCopy()` creates a `ReadOnlySvgElement`.
- Processing-instruction name/content storage appears reversed or lost.
- Shallow element copies share mutable attribute maps.
- Template shallow copies share the same mutable content list.
- Some factory entries discard prefixes.
- The large tag-flags table duplicates AngleSharp metadata manually and can drift.
- `TrackError` and several document lifecycle methods silently do nothing.
- `ReadOnlyDocument.DocumentElement => this` gives the document and document element unusual semantics.

These should become implemented features or explicitly unavailable capabilities, rather than nullable or no-op
approximations.

## Explicit feature profiles

Provide a small set of meaningful profiles:

```csharp
public enum ReadOnlyDomProfile
{
    Minimal,
    Navigable,
    SourceTracked,
    Diagnostic,
}
```

Detailed options can exist underneath the presets:

```csharp
[Flags]
public enum ReadOnlyDomFeatures
{
    None = 0,
    ParentLinks = 1 << 0,
    NamespaceInfo = 1 << 1,
    SourceReferences = 1 << 2,
    ParseErrors = 1 << 3,
    Comments = 1 << 4,
    ProcessingInstructions = 1 << 5,
}
```

The presets are important: callers should be able to select a documented cost model without understanding every
internal field.

### Metadata mapping decision

Do not create another complete parallel node hierarchy for every metadata level. That would multiply each correctness
fix across minimal, tracked, diagnostic, HTML-specialized, SVG, MathML, and future node variants.

Use one structural hierarchy and treat optional metadata as document-scoped auxiliary information. Named profiles are
the public UX; orthogonal feature flags are the internal model because source locations, prefixes, diagnostics, and
parent links do not have a strict dependency order.

Derive information before storing it:

- derive HTML, SVG, and MathML namespaces from existing node flags;
- derive node kind from the concrete node representation;
- derive the owner by walking parent links when parent links are enabled;
- store only non-empty prefixes;
- retain source offsets instead of full token objects unless full tokens are explicitly requested.

Optional metadata should live in stores owned by the document:

```csharp
internal sealed class ReadOnlyDocumentMetadata
{
    public Dictionary<ReadOnlyNode, SourceSpan>? SourceLocations;
    public Dictionary<ReadOnlyNode, StringOrMemory>? Prefixes;
    public List<ParseDiagnostic>? Diagnostics;
}
```

The minimal document should not allocate this object. Public lookup should be document-oriented, for example
`document.Metadata.TryGetSourceLocation(node, out location)`, so every minimal node does not need an owner or metadata
pointer.

AngleSharp currently assigns source information through `element.SourceReference = tag.ToHtmlToken()`. Three mappings
are possible:

1. A small tracked element subtype storing the source reference. This is the easiest prototype and leaves minimal nodes
   unchanged, but produces some duplication for specialized elements.
2. A tracked element forwarding assignments to a document metadata sink. This adds a sink reference plus sidecar costs
   and is unlikely to win when every element is tracked.
3. An upstream construction-factory hook such as `SetSourceReference(element, ref token)`. This is the cleanest design
   because the factory can write compact metadata directly without changing node layout.

Prototype the first option to establish cost and behavior, but prefer the third as the eventual AngleSharp boundary.
Avoid a full tracked clone of the DOM hierarchy.

Source metadata should have explicit fidelity levels:

```text
Offsets    start + length
Positions  offsets + line + column
Tokens     full AngleSharp source-reference/token representation
```

The profiles should map approximately as follows:

| Profile | Structure | Parents | Namespace | Source | Errors |
| --- | --- | --- | --- | --- | --- |
| Minimal | Yes | No | Derived | No | No |
| Navigable | Yes | Yes | Derived | No | No |
| SourceMapped | Yes | Yes | Derived plus sparse prefixes | Compact spans | No |
| Diagnostic | Yes | Yes | Complete | Configurable | Yes |
| TextProjection | No DOM | No | No | Optional output spans | Optional |

`TextProjection` remains a separate pipeline rather than a DOM metadata level.

### Capability APIs

Prefer capability-specific interfaces instead of values that silently disappear:

```csharp
public interface ISourceLocatedNode
{
    ISourceReference SourceReference { get; }
}

public interface INamespacedElement
{
    StringOrMemory NamespaceUri { get; }
    StringOrMemory Prefix { get; }
}
```

A minimal node should not implement these capabilities.

For source references enabled on every element, a tracked element subclass carrying one additional reference is likely
cheaper than a dictionary. A sidecar is preferable only for sparse annotations or after nodes have compact integer IDs.

## Text projection mode

An important additional mode is the cheapest possible way to turn HTML into dense, useful text for an LLM.

This should not be another DOM profile. It should be a tokenizer-driven projection that never creates nodes, parent
links, child lists, attribute maps, or attribute objects.

Suggested API shape:

```csharp
public static string ExtractText(
    ReadOnlyMemory<char> html,
    TextProjectionOptions options = default
);

public static void ExtractText(
    ReadOnlyMemory<char> html,
    IBufferWriter<char> output,
    TextProjectionOptions options = default
);
```

For streaming and bounded-memory workloads, also support a callback or writer-based API:

```csharp
public static void ExtractText(
    TextSource source,
    TextWriter output,
    TextProjectionOptions options = default
);
```

### Text projection behavior

The default LLM-oriented projection should:

- decode entities through the tokenizer;
- skip comments, processing instructions, scripts, styles, templates, and other non-visible content;
- collapse runs of whitespace;
- insert a single newline around meaningful block boundaries;
- preserve list-item and table-cell separation;
- optionally include selected semantic attributes such as `alt`, `title`, `aria-label`, and link destinations;
- avoid blank-line inflation;
- support a maximum output length;
- optionally stop tokenization when the output budget is reached;
- optionally restrict extraction to a token-filtered subtree.

Suggested presets:

```csharp
public enum TextProjectionProfile
{
    Dense,
    Readable,
    Accessible,
}
```

- `Dense`: text plus minimal separators; intended for token-efficient LLM input.
- `Readable`: preserves paragraph, heading, list, and table boundaries.
- `Accessible`: additionally emits useful `alt`, `title`, label, and ARIA text.

The implementation should maintain only a small stack or bitset of suppression/block state. It should not require the
HTML tree builder unless exact HTML error recovery proves necessary for acceptable text order.

Two implementations are worth comparing:

1. Direct tokenizer projection: cheapest and fastest, but must implement enough suppression and separator state.
2. Construction sink projection: uses AngleSharp's corrected tree-building order without retaining the tree. This may
   require a new upstream sink contract because the current builder expects stable node identities and mutation.

Start with direct tokenizer projection. Validate it against real malformed pages and compare its text with
`AngleSharp.Document.Body.TextContent`, not its DOM shape.

This mode may provide a larger practical allocation win than further shrinking the read-only DOM because it avoids
materialization entirely and produces the final LLM input in one pass.

## Further allocation opportunities in the current DOM

The largest costs are likely the auxiliary object graphs rather than the metadata fields already omitted:

- Every parent with children gets a `ReadOnlyNodeList`, a `List<ReadOnlyNode>`, and a backing array.
- Every attributed element gets a `ReadOnlyNamedNodeMap`, a `List<IConstructableAttr>`, a backing array, and one
  `ReadOnlyAttr` object per attribute.
- Every emitted text chunk becomes a separate text-node object.
- Every node retains a full `StringOrMemory` name even for known HTML tags.

Promising changes, in approximate priority order:

1. Coalesce adjacent text chunks during construction.
2. Replace `ReadOnlyNodeList + List<T>` with an inline-small collection holding the first one or two children directly,
   followed by an overflow array.
3. Apply the same strategy to attributes; most elements have very few.
4. Store compact known-tag IDs instead of full names on ordinary HTML elements.
5. Generate tag flags from canonical AngleSharp metadata instead of maintaining a delegate dictionary.
6. Add allocation-free traversal APIs using struct enumerators, visitors, or callbacks.
7. Offer a profile without parent links for forward-only scraping.
8. Strengthen tokenizer middleware so unwanted subtrees are never materialized.

The last item is particularly aligned with scraping: the cheapest node is one that is never constructed.

## Generic payload assessment

`ReadOnlyNode<TPayload>` is appropriate only when nearly every node needs the payload. For optional debug or
application data it is unattractive because:

- every node pays for the field;
- generic types spread through node lists, documents, elements, and APIs;
- different payload needs create incompatible DOM types;
- reference payloads still add a pointer per node;
- value payloads may enlarge every object substantially.

Prefer:

- capability-specific subclasses for dense features;
- document-owned sidecars for sparse features;
- callbacks during construction for extracting application data;
- indexed payload arrays if a compact DOM is introduced.

## Compact representation

A true compact representation would resemble:

```text
NodeRecord[]
  parent index
  first-child index
  next-sibling index
  first-attribute index
  attribute count
  tag/kind/flags

AttributeRecord[]
Text storage
Optional metadata arrays
```

This removes most per-node and collection objects. However, AngleSharp's current builder stores
`IConstructableElement` references in its open-element and formatting lists. A factory can provide smaller classes, but
not unboxed integer handles.

Two possible paths remain:

- Build the lightweight graph and then freeze/compact it. This lowers retained memory but raises peak memory and adds a
  second pass.
- Add an upstream AngleSharp tree-construction sink based on opaque handles or indices. This gives the best peak and
  retained memory but is a larger AngleSharp design change.

## Proposed cleanup sequence

### Phase 1: contract and correctness

- Define precisely what the minimal DOM promises.
- Fix MathML copy, processing instructions, document root/head/body, and shallow-copy aliasing.
- Add namespace and prefix tests.
- Replace silent metadata loss with explicit capabilities.
- Compare serialized trees with standard AngleSharp over a malformed-HTML corpus.

### Phase 2: text projection prototype

- Implement `Dense` projection directly over tokenizer output.
- Add suppression for non-visible content and block-aware separators.
- Support `IBufferWriter<char>`, `TextWriter`, output limits, and subtree filters.
- Compare output quality against AngleSharp `TextContent` on the existing page corpus.
- Benchmark allocation per input byte and output character, throughput, and peak retained memory.

### Phase 3: explicit DOM profiles

- Add `Minimal`, `Navigable`, `SourceTracked`, and `Diagnostic` profiles.
- Map tokenizer options and DOM features into those profiles.
- Ensure disabled features add no fields to minimal nodes.
- Document the retained-memory cost of every profile.

### Phase 4: current-model allocation work

- Coalesce text nodes.
- Introduce inline-small child and attribute collections.
- Simplify or generate tag metadata lookup.
- Add allocation-free traversal.
- Strengthen token-level subtree filtering.

Benchmark each change independently rather than combining representation changes into one opaque rewrite.

### Phase 5: compact prototype

Build a separate `CompactReadOnlyDocument` experiment. Measure:

- peak parse allocation;
- retained bytes after parsing;
- bytes per element, text node, and attribute;
- traversal and selector speed;
- compatibility-wrapper materialization cost.

### Phase 6: upstream proposal

If the compact prototype wins materially, propose an AngleSharp construction sink whose parser works with opaque
handles instead of `IConstructableNode` references.

## Immediate recommendation

Start with two parallel but small efforts:

1. Correct the existing minimal DOM contract so unsupported metadata is explicit.
2. Prototype direct tokenizer-to-dense-text projection.

The text projection is likely the fastest route to a meaningful new capability and the lowest allocation floor for
LLM-oriented scraping. The DOM remains valuable when traversal, relationships, or selective querying are required.

## Initial implementation and optimization results

The first implementation pass retained the object hierarchy but reduced the auxiliary collection graph:

- `SmallReferenceList<T>` stores the first two references inline and allocates an overflow array only when needed.
- Child nodes use the child itself as the internal singleton `IConstructableNodeList`; a collection object is allocated
  only when a second child is inserted.
- `ReadOnlyNamedNodeMap` represents its first attribute directly, avoiding a separate `ReadOnlyAttr` object for the
  common first-attribute case.
- Common and custom HTML tags bypass the allocating/hash-heavy creator dictionary path while preserving their parser
  flags.
- Namespace is derived from node flags with no per-node field.
- Prefix and source-reference metadata use sparse storage as an initial compatibility bridge.

On the `net10.0` in-process ShortRun GitHub body benchmark:

| Input/options | Original time | Optimized time | Original allocation | Optimized allocation |
| --- | ---: | ---: | ---: | ---: |
| Full attributes | 4.873 ms | 4.748 ms | 988.71 KB | 706.56 KB |
| Filtered attributes | 3.916 ms | 3.752 ms | 381.75 KB | 212.09 KB |

This is approximately 2.6% faster with 28.5% less allocation for the full-attribute case, and 4.2% faster with 44.4%
less allocation for the filtered case.

Across the 50 valid real-page inputs in `ParserBenchmark` compared with the previous `net10.0` ShortRun:

- all 50 read-only cases were faster;
- median time ratio was 0.76;
- all 50 allocated less;
- median allocation ratio was 0.58;
- the weakest allocation improvement was still approximately 13%.

A contiguous text-slice coalescing experiment was rejected: it saved effectively no allocation on the benchmark and
added about 1% execution time. Keep this benchmark gate for future plausible-but-unhelpful optimizations.
