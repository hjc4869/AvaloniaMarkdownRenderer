# AvaloniaMarkdown

A streaming, incremental GitHub-Flavored Markdown renderer for AvaloniaUI, built for real-time LLM
output rather than static documents.

It is designed around one question: **what does it cost to append a single token to a document that
is already 100 000 blocks long?** The answer is one reparsed block, one diff entry and one control
update — independent of document size.

```
 sections     blocks   tokens   avg us/token   p99 us
      100      1,602    7,007           3.46     11.3
    1,000      6,408    7,007           4.37     14.6
   10,000     54,468    7,007           3.01      6.8
   50,000    268,068    7,007           1.15      3.6
```

*(`dotnet run -c Release --project benchmarks/AvaloniaMarkdown.Benchmarks -- quick`; append latency
is flat, which is the whole design goal.)*

---

## Quick start

```csharp
var document = new MarkdownDocument();

var view = new MarkdownView
{
    Document = document,               // or: view.Bind(document)
    MarkdownTheme = MarkdownTheme.Dark,
    AutoScrollToEnd = true,
};

// Append from any thread — parsing happens on a pooled worker, never on the UI thread.
await foreach (string chunk in llmStream)
{
    document.Append(chunk);
}

document.Complete();                   // closes open blocks (named Complete because
                                       // Finalize is reserved by the CLR)
```

The document API is `Append`, `Replace`, `Clear`, `Reset`, `Complete` and `WaitForIdleAsync`.

### Running the demo

```bash
dotnet run --project samples/AvaloniaMarkdown.Demo
```

The demo streams a full-feature sample at a configurable token rate, can load a 268 000 block
document, and shows live append latency, realized-control count and GC statistics.

---

## Feature support

| Feature | Support |
| :--- | :--- |
| Headings (ATX `#`, setext `===` / `---`) | Yes |
| Paragraphs, soft/hard line breaks | Yes (soft breaks render as breaks by default, GitHub-comment style) |
| Bold, italic, bold-italic, strikethrough, inline code | Yes, via CommonMark delimiter-stack resolution |
| Lists: `-` `*` `+`, ordered, arbitrary nesting, tight/loose | Yes |
| Task lists `- [ ]` / `- [x]` | Yes, rendered as checkboxes |
| Block quotes, nested | Yes |
| Horizontal rules `---` `***` `___` | Yes |
| Fenced code blocks (```` ``` ````, `~~~`) | Yes: exact whitespace, monospace, language label, optional line numbers, selection, horizontal scroll |
| Indented code blocks | Yes |
| Links `[t](url "title")` | Yes: hover, hand cursor, tooltip, click, `LinkClicked` event, opens in browser |
| Autolinks `<url>`, bare `https://`, `www.`, `<a@b.com>` | Yes |
| Images `![alt](url)` | Yes: lazy, cancellable, memory + optional disk cache, decode-time downscaling |
| Tables with `:---`, `:---:`, `---:` alignment | Yes, with horizontal scrolling |
| HTML | Safe inline subset (`b i em strong code kbd mark s del ins u sub sup a br span`); everything else degrades to literal text |
| Escapes `\*`, HTML entities `&amp;` `&#65;` | Yes |
| Syntax highlighting | Deliberately out of scope; see `docs/EXTENDING.md` |
| Link reference definitions `[x]: url` | Not implemented; see `docs/EXTENDING.md` |

Deliberately unsupported constructs never throw and never break the surrounding document — they
degrade to plain text.

## Security posture

Markdown from an LLM is untrusted input.

* URLs pass through `UriSafety`, which allow-lists schemes. `javascript:`, `vbscript:`, `data:` for
  links and anything else unrecognised are dropped, so a crafted document cannot turn a click into
  code execution.
* Images accept only `http`, `https`, `file`, `data` and `avares`, with a 32 MB response cap and a
  30 second timeout.
* Disk cache file names are SHA-256 hashes of the URL, so a hostile URL cannot traverse out of the
  cache directory.
* Raw HTML is never interpreted as markup beyond a fixed inline allow list; `<script>`, `<iframe>`
  and friends are rendered as literal text.

---

## Documentation

* [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — the pipeline, every stage and the data
  structures that make it incremental.
* [`docs/INCREMENTAL-MODEL.md`](docs/INCREMENTAL-MODEL.md) — speculative tail parsing, block
  freezing, identity stability and diffing.
* [`docs/EXTENDING.md`](docs/EXTENDING.md) — extension points for new block kinds, inline
  constructs, themes and views.

## Repository layout

```
src/AvaloniaMarkdown           the library
samples/AvaloniaMarkdown.Demo  streaming demo shell
tests/AvaloniaMarkdown.Tests   parser, streaming and headless UI tests (91)
benchmarks/…Benchmarks         BenchmarkDotNet suite plus a `quick` profile
```

```bash
dotnet test
dotnet run -c Release --project benchmarks/AvaloniaMarkdown.Benchmarks -- quick   # fast profile
dotnet run -c Release --project benchmarks/AvaloniaMarkdown.Benchmarks -- --filter '*'   # full suite
```
