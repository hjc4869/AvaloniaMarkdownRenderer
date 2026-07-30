using System.Diagnostics;
using AvaloniaMarkdown.Diffing;
using AvaloniaMarkdown.Flattening;
using Xunit;

namespace AvaloniaMarkdown.Tests;

public class StreamingTests
{
    /// <summary>
    /// Feeding a document character by character must produce exactly the same result as parsing
    /// it in one shot. This is the core invariant of the speculative-line rollback.
    /// </summary>
    [Theory]
    [InlineData("# Heading\n\nParagraph with **bold** and `code`.\n")]
    [InlineData("- one\n- two\n  - nested\n- three\n")]
    [InlineData("```csharp\nConsole.WriteLine();\nvar x = 1;\n```\n")]
    [InlineData("| a | b |\n|---|---|\n| 1 | 2 |\n")]
    [InlineData("> quote\n> > nested quote\n\ntail\n")]
    [InlineData("- [ ] todo\n- [x] done\n")]
    [InlineData("Setext\n======\n\n---\n\nmore\n")]
    [InlineData("Text with a [link](https://example.com) and ![img](https://example.com/a.png)\n")]
    public void CharacterByCharacterStreaming_MatchesOneShotParse(string markdown)
    {
        MarkdownDocument streamed = TestDocument.Stream(markdown);
        streamed.Complete();

        MarkdownSnapshot expected = TestDocument.Parse(markdown);

        Assert.Equal(TestDocument.Describe(expected), TestDocument.Describe(streamed.Snapshot));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(64)]
    public void ChunkedStreaming_MatchesOneShotParse(int chunkSize)
    {
        const string Markdown = """
            # Report

            Intro paragraph with *emphasis*, **strong**, ~~gone~~ and `code`.

            1. first
            2. second
               - nested bullet
               - another

            > A quote
            > spanning lines

            ```python
            def f(x):
                return x * 2
            ```

            | Name | Value |
            |:-----|------:|
            | a    | 1     |
            | b    | 2     |

            ---

            Closing [link](https://example.com).
            """;

        MarkdownDocument streamed = TestDocument.StreamChunks(Markdown, chunkSize);
        streamed.Complete();

        Assert.Equal(
            TestDocument.Describe(TestDocument.Parse(Markdown)),
            TestDocument.Describe(streamed.Snapshot));
    }

    [Fact]
    public void PartialCodeFence_RendersProgressively()
    {
        MarkdownDocument document = TestDocument.Create();

        document.Append("Hello\n\n```csh\n");
        Assert.Equal(FlatBlockKind.Code, document.Snapshot[^1].Kind);
        Assert.Equal("csh", document.Snapshot[^1].Language);

        document.Append("Cons");
        Assert.Equal("Cons", document.Snapshot[^1].CodeText);

        document.Append("ole.Wri");
        Assert.Equal("Console.Wri", document.Snapshot[^1].CodeText);

        document.Append("teLine()\n");
        Assert.Equal("Console.WriteLine()", document.Snapshot[^1].CodeText);
        Assert.True(document.Snapshot[^1].IsOpen);

        document.Append("```\n");
        Assert.False(document.Snapshot[^1].IsOpen);
        Assert.Equal("Console.WriteLine()", document.Snapshot[^1].CodeText);
    }

    [Fact]
    public void PartialTable_BecomesATableOnceTheDelimiterRowArrives()
    {
        MarkdownDocument document = TestDocument.Create();

        document.Append("| a | b |\n");
        Assert.Equal(FlatBlockKind.Paragraph, document.Snapshot[0].Kind);

        document.Append("|---|");
        Assert.Equal(FlatBlockKind.Paragraph, document.Snapshot[0].Kind);

        document.Append("---|\n");
        Assert.Equal(FlatBlockKind.Table, document.Snapshot[0].Kind);

        document.Append("| 1 | 2 |\n");
        Assert.Single(document.Snapshot[0].Table!.Rows);
    }

    [Fact]
    public void UnfinishedEmphasis_IsClosedWhileStreaming()
    {
        MarkdownDocument document = TestDocument.Create();
        document.Append("this is **bol");

        Ast.InlineContent content = document.Snapshot[0].Inlines;
        Assert.Equal("this is bol", content.Text);
        Assert.Contains(content.Runs, r => (r.Style & Ast.InlineStyle.Bold) != 0);

        document.Append("d** done");
        Assert.Equal("this is bold done", document.Snapshot[0].Inlines.Text);
    }

    [Fact]
    public void UnfinishedEmphasis_CanBeDisabled()
    {
        var options = new MarkdownOptions { AutoCloseStreamingEmphasis = false };
        MarkdownDocument document = TestDocument.Create(options);
        document.Append("this is **bol");

        Assert.Equal("this is **bol", document.Snapshot[0].Inlines.Text);
    }

    [Fact]
    public void UnfinishedLink_DoesNotBreakTheParagraph()
    {
        MarkdownDocument document = TestDocument.Create();

        document.Append("see [the do");
        Assert.Equal("see [the do", document.Snapshot[0].Inlines.Text);

        document.Append("cs](https://exa");
        Assert.Contains("the docs", document.Snapshot[0].Inlines.Text);

        document.Append("mple.com) now");
        Assert.Equal("see the docs now", document.Snapshot[0].Inlines.Text);
        Assert.Equal("https://example.com", Assert.Single(document.Snapshot[0].Targets()).Url);
    }

    /// <summary>
    /// Identity stability is what keeps controls alive across token appends; without it the view
    /// would recreate the block on every character and the UI would flicker.
    /// </summary>
    [Fact]
    public void BlockIdentity_SurvivesTokenAppends()
    {
        MarkdownDocument document = TestDocument.Create();
        document.Append("Hello");

        int id = document.Snapshot[0].BlockId;

        for (int i = 0; i < 50; i++)
        {
            document.Append(" more");
            Assert.Equal(id, document.Snapshot[0].BlockId);
        }

        document.Append("\n");
        Assert.Equal(id, document.Snapshot[0].BlockId);
    }

    [Fact]
    public void CompletedBlocks_StayFrozenAndShared()
    {
        MarkdownDocument document = TestDocument.Create();
        document.Append("one\n\ntwo\n\n");

        MarkdownSnapshot before = document.Snapshot;
        Assert.Equal(2, before.StableCount);

        FlatBlock first = before[0];

        document.Append("three\n\nfour\n\n");
        MarkdownSnapshot after = document.Snapshot;

        Assert.True(after.SharesPrefixWith(before));
        Assert.Same(first, after[0]);
        Assert.Equal(4, after.StableCount);
    }

    /// <summary>A single appended token must not invalidate more than the block it touches.</summary>
    [Fact]
    public void AppendingAToken_ProducesASingleRenderOperation()
    {
        MarkdownDocument document = TestDocument.Create();
        var engine = new BlockDiffEngine();

        document.Append(string.Concat(Enumerable.Repeat("paragraph\n\n", 500)));
        document.Append("tail");

        MarkdownSnapshot before = document.Snapshot;
        document.Append(" token");
        MarkdownSnapshot after = document.Snapshot;

        IReadOnlyList<RenderOperation> operations = engine.Diff(before, after);

        RenderOperation operation = Assert.Single(operations);
        Assert.Equal(RenderOperationKind.UpdateInline, operation.Kind);
        Assert.Equal(500, operation.Index);
    }

    [Fact]
    public void ClosingABlock_EmitsFinalize()
    {
        MarkdownDocument document = TestDocument.Create();
        var engine = new BlockDiffEngine();

        document.Append("hello");
        MarkdownSnapshot before = document.Snapshot;

        document.Append("\n\n");
        MarkdownSnapshot after = document.Snapshot;

        Assert.Contains(engine.Diff(before, after), o => o.Kind == RenderOperationKind.FinalizeBlock);
    }

    [Fact]
    public void LargeCodeBlock_IsSplitIntoVirtualisableSegments()
    {
        var options = new MarkdownOptions { CodeBlockChunkLines = 64 };
        MarkdownDocument document = TestDocument.Create(options);

        document.Append("```\n");
        document.Append(string.Concat(Enumerable.Range(0, 500).Select(i => $"line {i}\n")));
        document.Append("```\n");

        FlatBlock[] segments = document.Snapshot.Where(b => b.Kind == FlatBlockKind.Code).ToArray();

        Assert.Equal(8, segments.Length);
        Assert.Equal(CodeSegmentRole.First, segments[0].SegmentRole);
        Assert.Equal(CodeSegmentRole.Last, segments[^1].SegmentRole);
        Assert.All(segments, s => Assert.Same(segments[0].CodeState, s.CodeState));
        Assert.Equal(500, segments[0].TotalLineCount);
    }

    [Fact]
    public void GrowingCodeBlock_ReusesCompletedSegments()
    {
        var options = new MarkdownOptions { CodeBlockChunkLines = 16 };
        MarkdownDocument document = TestDocument.Create(options);

        document.Append("```\n");
        document.Append(string.Concat(Enumerable.Range(0, 100).Select(i => $"line {i}\n")));

        FlatBlock firstSegment = document.Snapshot.First(b => b.Kind == FlatBlockKind.Code);

        document.Append("more\n");

        Assert.Same(firstSegment, document.Snapshot.First(b => b.Kind == FlatBlockKind.Code));
    }

    [Fact]
    public void ResetAndReplace_StartANewGeneration()
    {
        MarkdownDocument document = TestDocument.Create();
        document.Append("original\n\n");
        MarkdownSnapshot before = document.Snapshot;

        document.Replace("brand new\n\n");
        MarkdownSnapshot after = document.Snapshot;

        Assert.False(after.SharesPrefixWith(before));
        Assert.Equal("brand new", Assert.Single(after).Inlines.Text);

        document.Clear();
        Assert.Empty(document.Snapshot);
    }

    [Fact]
    public void VeryLargeDocument_StreamsInLinearTime()
    {
        MarkdownDocument document = TestDocument.Create();

        string paragraph = string.Concat(Enumerable.Repeat("word ", 20)) + "\n\n";
        var stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < 20_000; i++)
        {
            document.Append(paragraph);
        }

        stopwatch.Stop();

        Assert.Equal(20_000, document.Snapshot.Count);

        // Quadratic behaviour would take orders of magnitude longer than this bound.
        Assert.True(stopwatch.Elapsed.TotalSeconds < 20, $"Streaming took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task BackgroundMode_NeverParsesOnTheCallingThread()
    {
        var document = new MarkdownDocument(MarkdownOptions.Default, MarkdownProcessingMode.Background);
        int callerThread = Environment.CurrentManagedThreadId;
        int parseThread = callerThread;

        document.SnapshotChanged += (_, _) => parseThread = Environment.CurrentManagedThreadId;

        document.Append("# Title\n\nbody\n");
        document.Complete();
        await document.WaitForIdleAsync();

        Assert.NotEqual(callerThread, parseThread);
        Assert.Equal(2, document.Snapshot.Count);
    }

    [Fact]
    public async Task ConcurrentAppends_AreAppliedInOrder()
    {
        var document = new MarkdownDocument(MarkdownOptions.Default, MarkdownProcessingMode.Background);

        for (int i = 0; i < 1000; i++)
        {
            document.Append($"line {i}\n\n");
        }

        document.Complete();
        await document.WaitForIdleAsync();

        Assert.Equal(1000, document.Snapshot.Count);
        Assert.Equal("line 0", document.Snapshot[0].Inlines.Text);
        Assert.Equal("line 999", document.Snapshot[999].Inlines.Text);
    }
}

internal static class SnapshotExtensions
{
    public static Ast.InlineTarget[] Targets(this FlatBlock block) => block.Inlines.Targets;
}
