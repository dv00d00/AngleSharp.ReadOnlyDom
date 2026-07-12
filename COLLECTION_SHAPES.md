# Collection shape estimate (full, 47 documents)

Counts come from parsed Minimal trees. Byte estimates model x64 object/array alignment and geometric overflow growth; validate finalists with BenchmarkDotNet.

## Children per node

| Count | Owners | Share |
| ---: | ---: | ---: |
| 0 | 328,225 | 48.9% |
| 1 | 269,055 | 40.1% |
| 2 | 35,103 | 5.2% |
| 3 | 14,160 | 2.1% |
| 4 | 3,152 | 0.5% |
| 5+ | 21,673 | 3.2% |

## Attributes per element

| Count | Owners | Share |
| ---: | ---: | ---: |
| 0 | 136,912 | 38.1% |
| 1 | 158,791 | 44.2% |
| 2 | 42,747 | 11.9% |
| 3 | 13,785 | 3.8% |
| 4 | 2,730 | 0.8% |
| 5+ | 4,200 | 1.2% |

## Estimated child-list storage

The existing singleton representation handles child one. A list object is allocated only for nodes with at least two children.

| Inline slots | List objects | Overflow arrays | Estimated bytes |
| ---: | ---: | ---: | ---: |
| 1 | 74,088 | 74,088 | 8,079,032 |
| 2 | 74,088 | 38,985 | 7,371,672 |
| 4 | 74,088 | 21,673 | 7,533,688 |

## Estimated additional-attribute storage

The named map itself stores attribute one. Inline slots below cover only additional attributes. Capacity zero means array-backed overflow, not an attribute-free contract.

| Inline slots | Maps | Overflow arrays | Estimated bytes |
| ---: | ---: | ---: | ---: |
| 0 | 222,253 | 63,462 | 9,530,232 |
| 1 | 222,253 | 20,715 | 9,765,720 |
| 2 | 222,253 | 6,930 | 11,049,584 |
| 4 | 222,253 | 2,293 | 14,386,968 |
| no attributes emitted | 0 | 0 | 0 |

The last row requires a parser/factory contract that ignores token attributes and exposes an empty map; it is not equivalent to choosing inline capacity zero.
