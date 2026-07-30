# The incremental update model

This document explains the three mechanisms that make streaming cheap and flicker-free:
speculative tail parsing, block freezing, and identity-stable diffing.

---

## 1. The problem with a trailing partial line

A stream does not arrive on line boundaries:

````text
Hello
                     ← committed
```csh               ← committed
Cons                 ← partial: no newline yet
````

The renderer has to show `Cons` immediately, but `Cons` is not final — the next chunk may turn it
into `Console.WriteLine()`, or the line may turn out to be `Console` followed by a table delimiter
row, changing the block's type entirely.

Re-parsing the whole document on every token is O(n) per token and O(n²) per document. Re-parsing
only "the last block" is wrong, because a partial line can change which blocks exist.

## 2. Speculative parsing with an undo journal

`BlockParser` distinguishes two entry points:

```csharp
parser.ProcessCommittedLine(span);    // terminated by \n — permanent
parser.ProcessSpeculativeLine(span);  // the trailing partial line — reversible
```

During a speculative line every node that is about to be mutated is snapshotted exactly once
(`NodeSnapshot`, guarded by `MdNode.JournalMark`). The snapshot records child count, line count,
version, kind, open state and every kind-specific field.

`RollbackSpeculative()` replays the journal in reverse: child and line lists are truncated back to
their recorded lengths and scalars are restored. The tree is then bit-for-bit back at its last
committed state.

Each `Append` therefore runs:

```
BeginAppendCycle()      → roll back the previous speculative line
ProcessCommittedLine()  → for every newline that arrived
Flatten(promote: true)  → freeze whatever closed
ProcessSpeculativeLine()→ the new trailing partial line
Flatten(promote: false) → volatile tail only
EndAppendCycle()
```

Cost is proportional to the newly arrived text, not to the document.

### Why nodes are recycled, not recreated

Rollback removes nodes from the tree. If the next speculative parse allocated *new* nodes for the
same content, every block would get a new `BlockId` on every token, the diff would report
remove + append instead of update, and the view would destroy and rebuild the control — visible
flicker, lost selection, lost focus.

So rolled-back nodes go into a recycle bin ordered by creation sequence, and `RentNode` hands them
straight back out when the next parse asks for the same kind at the same position. Identity — and
therefore the Avalonia control — survives:

```csharp
[Fact] // StreamingTests
public void BlockIdentity_SurvivesTokenAppends()
{
    document.Append("Hello");
    int id = document.Snapshot[0].BlockId;

    for (int i = 0; i < 50; i++)
    {
        document.Append(" more");
        Assert.Equal(id, document.Snapshot[0].BlockId);   // same block, same control
    }
}
```

### Streaming-aware inline parsing

`**bol` is, strictly, literal asterisks followed by text — until the closing `**` arrives and it
retroactively becomes bold. Rendering it literally and then reflowing is exactly the flicker the
design is trying to avoid.

When a block is still open, `InlineParser` closes dangling emphasis openers at the end of the text
and marks the resulting runs `InlineStyle.Provisional`. `**bol` renders bold straight away and stays
bold. Set `MarkdownOptions.AutoCloseStreamingEmphasis = false` for strict CommonMark behaviour.

---

## 3. Freezing: what can never change again

A block is **closed** when no future line can extend it. Closed blocks are immutable, and that is
the invariant the entire publish path is built on.

The flattener tracks the *open path* — root → … → tip, always following the last child. At every
level, siblings before the open child are closed and can be promoted permanently:

```
Document
├── Heading      (closed)  ── frozen
├── Paragraph    (closed)  ── frozen
└── BlockQuote   (open)              ← Document.FlatFrozenChildIndex = 2
    ├── Paragraph (closed) ── frozen
    └── Paragraph (open)             ← BlockQuote.FlatFrozenChildIndex = 1
```

Because traversal is pre-order and the open child is always the last child, everything emitted
before the tip's own output forms a **prefix** of the pass output — so promotion is just "move the
first *k* entries into the frozen list".

Each container remembers `FlatFrozenChildIndex`, so the next pass starts there. A pass touches only
the open path.

Two exceptions are handled explicitly:

* **Open lists are never frozen.** A list's tightness can still flip from tight to loose when a
  blank line appears, which would change the spacing of already-frozen items.
* **Promotion only happens on committed state**, never while a speculative line is applied.

Once promoted, `MdNode.ReleaseRetainedState()` drops the node's line spans and caches.

## 4. Sharing the frozen prefix across snapshots

`FrozenBlockList` is append-only and chunked; chunks are allocated at full size and never resized,
and the count is published with a volatile write after the element store. Snapshots capture
`(frozenList, frozenCount, tailArray)`, so publishing costs one small array — never a copy of the
document.

```csharp
[Fact] // StreamingTests
public void CompletedBlocks_StayFrozenAndShared()
{
    document.Append("one\n\ntwo\n\n");
    var before = document.Snapshot;          // StableCount == 2

    document.Append("three\n\nfour\n\n");
    var after = document.Snapshot;           // StableCount == 4

    Assert.True(after.SharesPrefixWith(before));
    Assert.Same(before[0], after[0]);        // literally the same object
}
```

## 5. Diffing

Since both snapshots share the frozen list, entries below
`min(previous.StableCount, current.StableCount)` are identical *by construction*. The diff starts
there:

```csharp
[Fact] // StreamingTests
public void AppendingAToken_ProducesASingleRenderOperation()
{
    document.Append(string.Concat(Enumerable.Repeat("paragraph\n\n", 500)));
    document.Append("tail");

    var before = document.Snapshot;
    document.Append(" token");
    var after = document.Snapshot;

    var operation = Assert.Single(engine.Diff(before, after));
    Assert.Equal(RenderOperationKind.UpdateInline, operation.Kind);
    Assert.Equal(500, operation.Index);
}
```

One token in, one render operation out — with 501 blocks in the document or 500 001.

## 6. Applying operations to the view

`MarkdownVirtualizingPanel.ApplySnapshot` walks the operations:

| Operation | Effect |
| :--- | :--- |
| `AppendBlock` | push an estimated height onto the Fenwick tree (O(log n)) |
| `UpdateInline` | invalidate that height slot; if the block is realised, call `view.UpdateBlock(block)` — the control instance survives |
| `ReplaceBlock` | same, but recycle the control if the view type no longer matches |
| `InsertBlock` / `RemoveBlock` | structural: recycle realised controls and rebuild the height cache (still O(viewport) to re-realise) |
| `FinalizeBlock` | informational hook for views that render a streaming affordance |

Only realised blocks are touched. An `UpdateInline` for a block that is scrolled off-screen costs
one array write.

---

## 7. Verifying the invariant

The single most important correctness property is:

> **Streaming a document one character at a time must produce exactly the same result as parsing it
> in one shot.**

That is asserted directly, for every construct, in `StreamingTests`:

```csharp
[Theory]
[InlineData("# Heading\n\nParagraph with **bold** and `code`.\n")]
[InlineData("- one\n- two\n  - nested\n- three\n")]
[InlineData("```csharp\nConsole.WriteLine();\nvar x = 1;\n```\n")]
[InlineData("| a | b |\n|---|---|\n| 1 | 2 |\n")]
[InlineData("> quote\n> > nested quote\n\ntail\n")]
// …
public void CharacterByCharacterStreaming_MatchesOneShotParse(string markdown)
```

plus chunk sizes of 1, 3, 7 and 64 characters over a document containing every supported construct.
If the rollback journal, the recycle bin or the freeze boundary were wrong, these tests would fail.
