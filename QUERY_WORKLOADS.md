# Query-directed workload report

- Commit: `5ddd5eb`
- Runtime: `.NET 10.0.9`
- OS: `Microsoft Windows 10.0.26200`
- Iterations: `30` per engine/workload
- Time and total allocation cover parse, query, escaping output, and disposal. Retained bytes are incremental managed heap after parse and forced GC; pooled backing arrays already present in shared pools are excluded, so compact retained size is a lower bound. Peak live bytes are sampled and approximate.
- Output allocation is the measured cost of one escaping UTF-16 string copy. Logical counters describe the query implementation; selector internals that are opaque are conservatively counted as a full document scan.
- Decoded-value rate uses source entity markers divided by parsed attribute and text values as a reproducible corpus-level proxy.
- The streaming implementation is a deliberately limited well-formed-input lower bound, not a correctness candidate. AngleSharp core is the oracle.
- Oracle mismatches are retained in the report as findings so the pathological corpus can expose semantic gaps without suppressing the performance baseline.

## Workloads

| Workload | Query | Input | Pathological |
| --- | --- | ---: | :---: |
| Content text | first div#content -> normalized subtree text | 7,456 chars | no |
| Product cards | article.product -> sku, name, price, href | 23,954 chars | no |
| Head and body | title + meta description + first h1 + normalized body text | 10,925 chars | no |
| Adversarial content | first div#content over foster parenting, formatting adoption, template, and entities | 173 chars | yes |

## Structure

| Workload | Nodes | Max depth | P95 depth | Median subtree nodes | P95 subtree nodes | Median subtree text | P95 subtree text | Decoded-value rate |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Content text | 852 | 6 | 6 | 1 | 5 | 8 | 23 | 0.23% (1/444) |
| Product cards | 1,126 | 6 | 6 | 2 | 7 | 7 | 32 | 14.29% (160/1,120) |
| Head and body | 910 | 5 | 5 | 2 | 5 | 14 | 26 | 0.27% (1/364) |
| Adversarial content | 23 | 10 | 9 | 2 | 19 | 11 | 48 | 12.50% (1/8) |

## Repeated-query measurements

| Workload | Engine | Query-only mean | Query-only allocation |
| --- | --- | ---: | ---: |
| Content text | AngleSharp core | 287.0 us | 57.52 KB |
| Content text | Read-only DOM | 323.3 us | 49.99 KB |
| Content text | Compact arena | 184.7 us | 21.97 KB |
| Content text | Streaming lower bound | 82.2 us | 45.27 KB |
| Product cards | AngleSharp core | 1,609.3 us | 609.34 KB |
| Product cards | Read-only DOM | 1,401.3 us | 301.05 KB |
| Product cards | Compact arena | 874.9 us | 181.20 KB |
| Product cards | Streaming lower bound | 452.0 us | 203.17 KB |
| Head and body | AngleSharp core | 418.3 us | 86.58 KB |
| Head and body | Read-only DOM | 401.5 us | 95.93 KB |
| Head and body | Compact arena | 250.2 us | 44.92 KB |
| Head and body | Streaming lower bound | 122.5 us | 75.45 KB |
| Adversarial content | AngleSharp core | 16.0 us | 2.73 KB |
| Adversarial content | Read-only DOM | 11.1 us | 1.37 KB |
| Adversarial content | Compact arena | 2.6 us | 0.31 KB |
| Adversarial content | Streaming lower bound | 5.9 us | 0.78 KB |

## Reuse break-even

Estimated from `parse = end-to-end - query-only` and `total(N) = parse + N * query`. Values compare compact arena with read-only DOM on one retained document.

| Workload | Compact beats ROD at | ROD parse estimate | Compact parse estimate |
| --- | ---: | ---: | ---: |
| Content text | 3 queries | 1,171.8 us | 1,470.7 us |
| Product cards | 2 queries | 3,009.7 us | 3,717.6 us |
| Head and body | 2 queries | 2,574.5 us | 2,876.2 us |
| Adversarial content | 5 queries | 69.2 us | 108.7 us |

## End-to-end measurements

| Workload | Engine | Mean | Allocated | Incremental retained | Approx. peak live | Output allocation | Output chars | Oracle match |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | :---: |
| Content text | AngleSharp core | 1,942.2 us | 417.37 KB | 223.47 KB | 369.39 KB | 5.47 KB | 2,786 | yes |
| Content text | Read-only DOM | 1,495.2 us | 236.74 KB | 60.84 KB | 200.75 KB | 5.47 KB | 2,786 | yes |
| Content text | Compact arena | 1,655.4 us | 180.85 KB | 1.21 KB | 168.62 KB | 5.47 KB | 2,786 | yes |
| Content text | Streaming lower bound | 105.0 us | 45.29 KB | 0.00 KB | 8.03 KB | 5.47 KB | 2,786 | yes |
| Product cards | AngleSharp core | 6,163.8 us | 1,392.60 KB | 416.44 KB | 666.55 KB | 12.33 KB | 6,300 | yes |
| Product cards | Read-only DOM | 4,411.0 us | 614.84 KB | 128.76 KB | 329.24 KB | 12.33 KB | 6,300 | yes |
| Product cards | Compact arena | 4,592.5 us | 428.68 KB | 1.34 KB | 256.95 KB | 12.33 KB | 6,300 | yes |
| Product cards | Streaming lower bound | 439.0 us | 203.20 KB | 0.00 KB | 8.03 KB | 12.33 KB | 6,300 | yes |
| Head and body | AngleSharp core | 3,212.3 us | 560.62 KB | 272.26 KB | 489.88 KB | 8.86 KB | 4,522 | yes |
| Head and body | Read-only DOM | 2,976.0 us | 358.35 KB | 68.79 KB | 273.02 KB | 8.86 KB | 4,522 | yes |
| Head and body | Compact arena | 3,126.4 us | 276.99 KB | 1.21 KB | 240.91 KB | 8.86 KB | 4,522 | yes |
| Head and body | Streaming lower bound | 131.7 us | 75.48 KB | 0.00 KB | 8.03 KB | 8.86 KB | 4,522 | yes |
| Adversarial content | AngleSharp core | 153.0 us | 36.04 KB | 20.84 KB | 48.08 KB | 0.05 KB | 15 | yes |
| Adversarial content | Read-only DOM | 80.3 us | 8.48 KB | 3.09 KB | 16.06 KB | 0.05 KB | 15 | yes |
| Adversarial content | Compact arena | 111.3 us | 24.34 KB | 0.12 KB | 40.02 KB | 0.05 KB | 15 | yes |
| Adversarial content | Streaming lower bound | 6.2 us | 0.81 KB | 0.00 KB | 8.03 KB | 0.11 KB | 44 | no |

## Query counters

| Workload | Engine | Nodes inspected | Attributes inspected | Text nodes inspected | Nodes retained | Attributes retained | Input consumed | Decoded values | Retained / inspected |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Content text | AngleSharp core | 852 | 1 | 443 | 1 | 0 | 7,456 | 1 | 0.12% |
| Content text | Read-only DOM | 852 | 1 | 443 | 1 | 0 | 7,456 | 1 | 0.12% |
| Content text | Compact arena | 852 | 1 | 443 | 1 | 0 | 7,456 | 1 | 0.12% |
| Content text | Streaming lower bound | 809 | 1 | 442 | 1 | 0 | 7,418 | 1 | 0.12% |
| Product cards | AngleSharp core | 1,126 | 640 | 480 | 160 | 320 | 23,954 | 160 | 14.21% |
| Product cards | Read-only DOM | 1,126 | 640 | 480 | 160 | 320 | 23,954 | 160 | 14.21% |
| Product cards | Compact arena | 1,126 | 640 | 480 | 160 | 320 | 23,954 | 160 | 14.21% |
| Product cards | Streaming lower bound | 1,287 | 640 | 480 | 160 | 320 | 23,954 | 160 | 12.43% |
| Head and body | AngleSharp core | 910 | 2 | 362 | 4 | 1 | 10,925 | 1 | 0.44% |
| Head and body | Read-only DOM | 910 | 2 | 362 | 4 | 1 | 10,925 | 1 | 0.44% |
| Head and body | Compact arena | 910 | 2 | 362 | 4 | 1 | 10,925 | 1 | 0.44% |
| Head and body | Streaming lower bound | 1,092 | 2 | 362 | 4 | 1 | 10,925 | 1 | 0.37% |
| Adversarial content | AngleSharp core | 23 | 1 | 7 | 1 | 0 | 173 | 1 | 4.35% |
| Adversarial content | Read-only DOM | 23 | 1 | 7 | 1 | 0 | 173 | 1 | 4.35% |
| Adversarial content | Compact arena | 23 | 1 | 7 | 1 | 0 | 173 | 1 | 4.35% |
| Adversarial content | Streaming lower bound | 19 | 1 | 7 | 1 | 0 | 173 | 1 | 5.26% |

## Recommendation

The first integrated prototype should be only `first div#content -> normalized subtree text`, not a DSL or general extraction architecture. Implement it directly over the tokenizer with an explicit result type and AngleSharp core as the correctness oracle; add product cards and head/body extraction only after that gate. The deliberately limited streaming lower bound shows the available ceiling, while its pathological mismatch shows why it cannot become production code unchanged.

Keep the compact arena as the reusable-document option: it reduces repeated-query cost and allocation, and the break-even table shows when its higher construction cost is recovered. Compact and read-only DOM both match AngleSharp core on the pathological extraction; the deliberately limited streaming scanner remains intentionally non-conforming.

## Reproduce

```powershell
./scripts/bench.ps1 query
# Fast correctness and wiring check:
dotnet run --project ./AngleSharp.ReadOnlyDom.Benchmarks -c Release -f net10.0 -- --query-workloads --iterations 1
```
