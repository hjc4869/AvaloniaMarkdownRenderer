# Extending the renderer

Extension points are ordered from cheapest to deepest.

---

## Theming

`MarkdownTheme` is a plain object with `init` properties, so a variant is one expression:

```csharp
view.MarkdownTheme = new MarkdownTheme
{
    FontSize = 15,
    CodeFontFamily = new FontFamily("JetBrains Mono"),
    LinkForeground = Brushes.MediumPurple,
    ShowCodeLineNumbers = true,
    ListIndent = 28,
};
```

Typefaces are resolved lazily and cached per (bold, italic, monospace) combination. Assigning a new
theme calls `MarkdownVirtualizingPanel.Rebuild()`, which recycles every control and re-measures —
correct, and cheap because only the viewport is realised.

## Parser options

```csharp
var options = new MarkdownOptions
{
    SoftLineBreaksAsHardBreaks = true,   // GitHub-comment behaviour; what chat UIs want
    AutoCloseStreamingEmphasis = true,   // `**bol` renders bold immediately
    CodeBlockChunkLines = 128,           // virtualisation granularity for code blocks
    EnableTables = true,
    EnableTaskLists = true,
    EnableStrikethrough = true,
    EnableAutoLinks = true,
};

var document = new MarkdownDocument(options);
```

Options are fixed for a document's lifetime; create a new document to change them.

## Handling links yourself

```csharp
view.LinkClicked += (_, e) =>
{
    if (e.Url.StartsWith("app://", StringComparison.Ordinal))
    {
        Navigate(e.Url);
        e.Handled = true;   // suppress the default "open in browser"
    }
};
```

New schemes must also be added to `UriSafety.AllowedSchemes`, otherwise the parser drops the
destination before it ever reaches a view. That ordering is deliberate: the allow list is the
security boundary.

## Image loading

```csharp
view.ImageCache = new MarkdownImageCache(myHttpClient)
{
    DiskCacheDirectory = Path.Combine(cacheRoot, "markdown-images"),
    MaxMemoryBytes = 64L * 1024 * 1024,
    BaseUri = new Uri("https://cdn.example.com/"),   // resolves relative image URLs
};
```

To take over loading entirely, implement `IMarkdownHost` on your own control — `MarkdownView` is
just one implementation of it.

---

## Adding a new block kind

Four steps, following how tables were added:

**1. Recognise it in the parser.** Add a `BlockKind`, then a start condition in
`BlockParser.ProcessLineCore` phase 4 and, if the block spans multiple lines, a continuation rule in
phase 2. Keep the invariant that only the open path is mutated.

**2. Materialise it in the flattener.** Add a `FlatBlockKind`, an `Emit…` method in
`DocumentFlattener`, and any payload fields on `FlatBlock`. Payloads must be immutable and fully
materialised — no references back into `MdNode`.

**3. Write the view.** Derive from `MarkdownBlockView` and implement `MeasureContent` plus
`RenderContent`. Quote bars, list markers, task checkboxes and indentation are handled by the base
class. Cache expensive layout keyed on `Block.Version` and the available width, as
`RichTextBlockView` does.

**4. Register it** in `MarkdownVirtualizingPanel.Rent`, `ViewKind` and `PoolKind`.

### Worked sketch: admonitions

```text
> [!WARNING]
> Body text.
```

* Parser: when opening a block quote, peek for `[!TYPE]` on the first line, record it on the node.
* Flattener: carry `AdmonitionKind` onto the flat blocks of that subtree.
* View: `RichTextBlockView` already handles the text; add a coloured bar and icon in
  `RenderContent`, or subclass it.

No change to the incremental machinery is needed — freezing, diffing and virtualisation are
kind-agnostic.

## Adding a new inline construct

Add a case to `InlineParser.Tokenize`, then either:

* emit a token that contributes a `StyleSpan` (like emphasis and links), or
* add a flag to `InlineStyle` and map it to `TextRunProperties` in
  `InlineTextRenderer.BuildOverrides`.

`InlineStyle` has spare bits. `Highlight`, `Superscript` and `Subscript` are already wired end to
end and are good templates.

### Footnotes, mentions, emoji

* **Emoji / mentions** — pure tokenizer work: recognise `:smile:` or `@user` and emit a `Literal`
  or styled token. No block-level change.
* **Footnotes** — needs a document-level definition map. Collect `[^id]: …` definitions when the
  defining block *closes* (they are immutable from then on) and resolve references at flatten time.
  Same mechanism would implement link reference definitions (`[label]: url`), which are currently
  the main CommonMark gap.

## Syntax highlighting

Deliberately not implemented, but the seam exists. `FlatBlock.CodeText` and `FlatBlock.Language`
carry everything a highlighter needs, and `CodeSegmentView` already builds a `TextLayout` per
segment.

To add it, produce an `InlineRun[]` for the segment text and feed it through
`InlineTextRenderer.CreateLayout` instead of the plain `TextLayout` constructor. Run the highlighter
on the parser thread inside `DocumentFlattener.EmitCode` so the UI thread stays free — and note that
segment-level caching already means only the last segment of a streaming code block is re-highlighted
per token.

## Math and Mermaid

Both are block-level renderers that need an async, potentially expensive layout step:

1. Recognise `$$…$$` / ```` ```mermaid ```` as a new block kind.
2. Emit a flat block carrying the raw source.
3. In the view, render a placeholder immediately, kick off rendering on a background thread, and
   call `IMarkdownHost.InvalidateBlockMeasure(this)` when it finishes.

`ImageBlockView` is the reference implementation of exactly that pattern, including cancellation
when the block scrolls out of view.

## Collapsible sections

`FlatBlock` carries `IsOpen`, `IndentLevel` and `QuoteDepth`; a collapsible section needs one more
piece of state — a set of collapsed block ids owned by the view. On toggle, filter the snapshot
before handing it to the panel. Because the panel diffs by key, collapsing emits `RemoveBlock`
operations rather than a rebuild.

---

## Testing extensions

Mirror the existing structure:

* `BlockParserTests` — block recognition, in one-shot form.
* `InlineParserTests` — inline runs, using `TestDocument.StyleMap` to assert per-character styling.
* `StreamingTests` — **always** add the construct to
  `CharacterByCharacterStreaming_MatchesOneShotParse`. If a construct is not incrementally correct,
  that is where it shows up.
* `RenderingTests` — headless Avalonia with real Skia, for view realisation and interaction.
