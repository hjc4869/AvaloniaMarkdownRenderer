# Architecture

## The pipeline

```
 Append(chunk)  ── any thread
        │
        ▼
 command queue ──────────────── serialised, order-preserving
        │
        ▼  (pooled worker thread — never the UI thread)
 TextBuffer            contiguous char[] , O(1) span slicing
        │
        ▼
 BlockParser           one line at a time, mutates only the open path
        │
        ▼
 MdNode tree           mutable while open, immutable once closed
        │
        ▼
 InlineParser          per changed block: tokenise → links → emphasis → runs
        │
        ▼
 DocumentFlattener     pre-order projection to a linear render list
        │
        ▼
 MarkdownSnapshot      immutable; frozen prefix shared by reference
        │
        ▼  ── Dispatcher, coalesced to at most one update per frame
 BlockDiffEngine       keyed prefix/suffix diff → RenderOperation[]
        │
        ▼
 MarkdownVirtualizingPanel  applies ops, realises only the viewport
        │
        ▼
 MarkdownBlockView         one control per visible block, recycled
```

Each stage is a separate type with no Avalonia dependency below `Rendering/`, so the parser,
flattener and diff engine are all unit-testable without a UI.

---

## Stage 1 — `TextBuffer`

`src/AvaloniaMarkdown/Text/TextBuffer.cs`

An append-only `char[]` rented from `ArrayPool`, doubling on growth. Keeping the document
contiguous means every later stage can address content as `(start, length)` pairs (`SourceSpan`)
and read it as `ReadOnlySpan<char>` with no allocation. A 1 M character document costs 2 MB and at
most 20 copies over its entire lifetime.

## Stage 2 — `BlockParser`

`src/AvaloniaMarkdown/Parsing/BlockParser.cs`

A CommonMark-style line-driven state machine. For each line it:

1. Walks down the chain of open containers (block quotes, lists, list items) and matches their
   prefixes with a tab-aware `LineCursor`.
2. Continues an open leaf (paragraph, fenced code, table, HTML block) if it can.
3. Applies lazy paragraph continuation.
4. Closes unmatched containers and tries to start new blocks.
5. Adds the remaining text to the deepest open leaf.

The critical property is that **only the open path is ever touched**. Closed blocks are never
revisited, which is why parsing cost is proportional to the new text rather than the document.

Paragraph→heading (setext) and paragraph→table conversions are the only in-place kind changes, and
both happen while the paragraph is still open.

## Stage 3 — `InlineParser`

`src/AvaloniaMarkdown/Parsing/InlineParser.cs`

Runs per changed block, in five passes:

1. **Tokenise** — escapes, HTML entities, code spans, `<url>` and bare autolinks, safe inline HTML
   tags, brackets, and `* _ ~` delimiter runs classified as left/right flanking.
2. **Links** — a bracket stack resolves `[text](url "title")` and `![alt](url)`.
3. **Emphasis** — the CommonMark delimiter-stack algorithm including the "rule of three".
4. **Materialise** — build the display string, recording each token's output range.
5. **Flatten** — a sweep line over the overlapping style spans produces a sorted, non-overlapping
   `InlineRun[]`.

The output is an immutable `InlineContent`: one `string` plus a flat run table. All working buffers
live on the reused parser instance.

## Stage 4 — `DocumentFlattener`

`src/AvaloniaMarkdown/Flattening/DocumentFlattener.cs`

Projects the tree onto a linear list of `FlatBlock`s — the currency between threads. A `FlatBlock`
contains only materialised values (strings, run tables, table models, image URLs) and never a
reference back into the mutable AST, which is what makes the pipeline thread-safe without locking.

Two structural decisions matter:

* **Flattening rather than nesting.** A list item's paragraph becomes a top-level entry carrying
  `IndentLevel`, `Marker` and `QuoteDepth`. Virtualisation then only has to deal with a flat list.
* **Code segmentation.** A code block longer than `MarkdownOptions.CodeBlockChunkLines` (default
  128) is split into segments keyed `(BlockId, SegmentIndex)`. A 10 000 line listing becomes 79
  independently virtualised items that still share one `CodeBlockState` for horizontal scrolling
  and selection, so it behaves as a single block.

## Stage 5 — `MarkdownSnapshot` and `FrozenBlockList`

Publishing a snapshot must not copy the document. Instead a snapshot is
`frozen prefix (shared by reference) + small volatile tail (array)`.

`FrozenBlockList` is an append-only chunked list: chunk arrays are allocated full size and never
resized, and the element count is published with a volatile write *after* the element store. A
single writer (the parser thread) and any number of readers therefore need no lock.

## Stage 6 — `BlockDiffEngine`

`src/AvaloniaMarkdown/Diffing/BlockDiffEngine.cs`

Because two snapshots of the same generation share their frozen prefix, the diff starts at
`min(previous.StableCount, current.StableCount)` rather than index 0. It then does a keyed
prefix/suffix trim and emits:

| Operation | Meaning |
| :--- | :--- |
| `AppendBlock` | new block at the end |
| `InsertBlock` | new block in the middle |
| `ReplaceBlock` | same slot, different kind — control must be rebuilt |
| `UpdateInline` | same key and kind — control updates in place |
| `RemoveBlock` | block disappeared |
| `FinalizeBlock` | block stopped streaming |

There is no `Clear` operation; even `Replace()` of the whole document is expressed as a keyed diff.

## Stage 7 — `MarkdownVirtualizingPanel`

`src/AvaloniaMarkdown/Rendering/MarkdownVirtualizingPanel.cs`

Implements `ILogicalScrollable`, so the enclosing `ScrollViewer` delegates scrolling instead of
laying out the document. Block extents live in `BlockHeightCache`, a Fenwick tree that answers
both required queries in O(log n):

* `PrefixSum(i)` — the pixel offset of block *i*
* `FindIndex(y)` — the block covering pixel *y*

Unmeasured blocks contribute a per-kind running estimate that is **frozen at insertion time**.
Improving the estimate later therefore cannot retroactively move content the user is looking at.

Only blocks intersecting the viewport plus `OverscanPixels` (default 240) are realised; the rest
stay as `FlatBlock` model objects. Controls are recycled through per-kind pools. In the test suite,
scrolling through a 2 000 block document materialises fewer than 200 distinct controls in total.

## Stage 8 — Block views

Every block owns exactly one control, all deriving from `MarkdownBlockView`, which draws the shared
chrome (quote bars, list markers, task checkboxes, indentation) so concrete views only handle their
own content.

| View | Blocks |
| :--- | :--- |
| `RichTextBlockView` | paragraph, heading, degraded HTML |
| `CodeSegmentView` | one segment of a code block |
| `TableBlockView` | a whole table |
| `ThematicBreakView` | horizontal rules |
| `ImageBlockView` | images |

### Text rendering

Inline formatting is **not** expressed with nested controls. A paragraph containing bold, italic,
code and link fragments is a single `TextLayout` whose styling comes from
`IReadOnlyList<ValueSpan<TextRunProperties>>` derived from the block's `InlineRun[]`
(`InlineTextRenderer`). One block equals one visual and one shaping pass.

Link hit-testing uses `TextLayout.HitTestPoint` mapped back through `InlineContent.FindRun` — no
per-fragment controls are needed for hover or click either.

---

## Threading model

| Thread | Work |
| :--- | :--- |
| Any | `Append` / `Replace` / `Clear` / `Complete` — enqueue only |
| Pooled worker | block parsing, inline parsing, flattening, snapshot publication |
| Background | image download and decode |
| UI | diff, applying render operations, measure/arrange/draw |

`MarkdownDocument` serialises commands through a queue guarded by one lock; exactly one worker is
active at a time, so the parser is effectively single-threaded and needs no internal synchronisation.

`MarkdownView` receives `SnapshotChanged` on the worker thread, stores the latest snapshot and lets
a 16 ms dispatcher timer apply it. A burst of 1 000 appends therefore produces at most ~60 visual
updates per second, and `DeferUpdatesWhileScrolling` holds updates back for `ScrollQuietPeriod`
(120 ms) after genuine user scroll input, bounded by `MaximumDeferral` (400 ms).

Note that *content-driven* `ScrollChanged` events are explicitly ignored when deciding whether the
user is scrolling; treating them as user activity would stall streaming indefinitely.

---

## Memory characteristics

Measured on a 8.3 MB / 267 000 block document: **≈ 237 MB retained, ≈ 930 bytes per block.**

That covers the source buffer, the AST, the materialised render list and the parsed inline runs.
Contributing decisions:

* Once a block's output is promoted into the frozen prefix, `MdNode.ReleaseRetainedState()` drops
  its line spans and caches — the node is never visited again.
* Inline parsing results are cached per `(node, version)` so a growing tail block reparses only
  itself.
* Completed code segments are reused verbatim as new lines stream in.
* The parser reuses `StringBuilder`s, span lists, delimiter stacks and token lists across calls; a
  steady-state append allocates only the strings and arrays that end up in the snapshot.

The dominant remaining cost is the `FlatBlock` object itself plus the per-block display string and
run array. If a deployment needs to go lower, splitting `FlatBlock`'s code/table/image fields into a
side object would remove roughly 100 bytes from every paragraph.
