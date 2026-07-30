using AvaloniaMarkdown.Ast;
using AvaloniaMarkdown.Flattening;
using Xunit;

namespace AvaloniaMarkdown.Tests;

public class BlockParserTests
{
    [Theory]
    [InlineData("# H1", 1)]
    [InlineData("## H2", 2)]
    [InlineData("###### H6", 6)]
    public void AtxHeadings_ProduceHeadingBlocks(string markdown, int level)
    {
        MarkdownSnapshot snapshot = TestDocument.Parse(markdown);

        FlatBlock block = Assert.Single(snapshot);
        Assert.Equal(FlatBlockKind.Heading, block.Kind);
        Assert.Equal(level, block.HeadingLevel);
    }

    [Fact]
    public void SevenHashes_IsNotAHeading()
    {
        MarkdownSnapshot snapshot = TestDocument.Parse("####### nope");

        FlatBlock block = Assert.Single(snapshot);
        Assert.Equal(FlatBlockKind.Paragraph, block.Kind);
    }

    [Fact]
    public void AtxHeading_ClosingHashesAreStripped()
    {
        MarkdownSnapshot snapshot = TestDocument.Parse("## Title ##");

        Assert.Equal("Title", snapshot[0].Inlines.Text);
    }

    [Fact]
    public void SetextHeading_ConvertsPrecedingParagraph()
    {
        MarkdownSnapshot snapshot = TestDocument.Parse("Title\n===\n\nBody");

        Assert.Equal(FlatBlockKind.Heading, snapshot[0].Kind);
        Assert.Equal(1, snapshot[0].HeadingLevel);
        Assert.Equal("Title", snapshot[0].Inlines.Text);
        Assert.Equal(FlatBlockKind.Paragraph, snapshot[1].Kind);
    }

    [Theory]
    [InlineData("---")]
    [InlineData("***")]
    [InlineData("___")]
    [InlineData(" - - - ")]
    public void ThematicBreaks_AreRecognised(string markdown)
    {
        MarkdownSnapshot snapshot = TestDocument.Parse(markdown);

        Assert.Equal(FlatBlockKind.ThematicBreak, Assert.Single(snapshot).Kind);
    }

    [Fact]
    public void Paragraphs_AreSeparatedByBlankLines()
    {
        MarkdownSnapshot snapshot = TestDocument.Parse("one\n\ntwo\n\nthree");

        Assert.Equal(3, snapshot.Count);
        Assert.All(snapshot, b => Assert.Equal(FlatBlockKind.Paragraph, b.Kind));
    }

    [Fact]
    public void NestedBlockQuotes_TrackDepth()
    {
        MarkdownSnapshot snapshot = TestDocument.Parse("> outer\n>\n> > inner");

        Assert.Equal(1, snapshot[0].QuoteDepth);
        Assert.Equal("outer", snapshot[0].Inlines.Text);
        Assert.Equal(2, snapshot[1].QuoteDepth);
        Assert.Equal("inner", snapshot[1].Inlines.Text);
    }

    [Fact]
    public void UnorderedList_ProducesMarkers()
    {
        MarkdownSnapshot snapshot = TestDocument.Parse("- a\n- b\n- c");

        Assert.Equal(3, snapshot.Count);
        Assert.All(snapshot, b => Assert.Equal("\u2022", b.Marker));
        Assert.All(snapshot, b => Assert.Equal(1, b.IndentLevel));
    }

    [Fact]
    public void OrderedList_NumbersFromStart()
    {
        MarkdownSnapshot snapshot = TestDocument.Parse("3. a\n4. b");

        Assert.Equal("3.", snapshot[0].Marker);
        Assert.Equal("4.", snapshot[1].Marker);
    }

    [Fact]
    public void NestedLists_IncreaseIndentLevel()
    {
        MarkdownSnapshot snapshot = TestDocument.Parse("- a\n  - b\n    - c");

        Assert.Equal(1, snapshot[0].IndentLevel);
        Assert.Equal(2, snapshot[1].IndentLevel);
        Assert.Equal(3, snapshot[2].IndentLevel);
    }

    [Fact]
    public void TaskLists_CaptureCheckedState()
    {
        MarkdownSnapshot snapshot = TestDocument.Parse("- [ ] todo\n- [x] done");

        Assert.False(snapshot[0].TaskChecked);
        Assert.Equal("todo", snapshot[0].Inlines.Text);
        Assert.True(snapshot[1].TaskChecked);
        Assert.Equal("done", snapshot[1].Inlines.Text);
    }

    [Fact]
    public void FencedCode_PreservesWhitespaceAndLanguage()
    {
        MarkdownSnapshot snapshot = TestDocument.Parse("```csharp\n    var x = 1;\n\nend\n```");

        FlatBlock block = Assert.Single(snapshot);
        Assert.Equal(FlatBlockKind.Code, block.Kind);
        Assert.Equal("csharp", block.Language);
        Assert.Equal("    var x = 1;\n\nend", block.CodeText);
    }

    [Fact]
    public void FencedCode_MarkdownInsideIsNotParsed()
    {
        MarkdownSnapshot snapshot = TestDocument.Parse("```\n# not a heading\n- not a list\n```");

        FlatBlock block = Assert.Single(snapshot);
        Assert.Equal("# not a heading\n- not a list", block.CodeText);
    }

    [Fact]
    public void IndentedCode_IsRecognised()
    {
        MarkdownSnapshot snapshot = TestDocument.Parse("text\n\n    code line\n\nmore");

        Assert.Equal(FlatBlockKind.Code, snapshot[1].Kind);
        Assert.Equal("code line", snapshot[1].CodeText);
    }

    [Fact]
    public void Tables_ParseAlignmentsAndRows()
    {
        MarkdownSnapshot snapshot = TestDocument.Parse(
            "| Left | Center | Right |\n|:-----|:------:|------:|\n| a | b | c |\n| d | e | f |");

        FlatBlock block = Assert.Single(snapshot);
        Assert.Equal(FlatBlockKind.Table, block.Kind);

        TableModel table = block.Table!;
        Assert.Equal(new[] { TableAlignment.Left, TableAlignment.Center, TableAlignment.Right }, table.Alignments);
        Assert.Equal(new[] { "Left", "Center", "Right" }, table.Header.Select(c => c.Text));
        Assert.Equal(2, table.Rows.Length);
        Assert.Equal(new[] { "d", "e", "f" }, table.Rows[1].Select(c => c.Text));
    }

    [Fact]
    public void Table_EndsAtBlankLine()
    {
        MarkdownSnapshot snapshot = TestDocument.Parse("| a |\n|---|\n| b |\n\nafter");

        Assert.Equal(FlatBlockKind.Table, snapshot[0].Kind);
        Assert.Equal(FlatBlockKind.Paragraph, snapshot[1].Kind);
        Assert.Equal("after", snapshot[1].Inlines.Text);
    }

    [Fact]
    public void CodeBlockInsideList_KeepsIndentLevel()
    {
        MarkdownSnapshot snapshot = TestDocument.Parse("- item\n\n  ```\n  code\n  ```");

        Assert.Equal(FlatBlockKind.Paragraph, snapshot[0].Kind);
        Assert.Equal(FlatBlockKind.Code, snapshot[1].Kind);
        Assert.Equal("code", snapshot[1].CodeText);
        Assert.Equal(1, snapshot[1].IndentLevel);
    }

    [Fact]
    public void HtmlBlock_DegradesToText()
    {
        MarkdownSnapshot snapshot = TestDocument.Parse("<div class=\"x\">\nhello\n</div>");

        FlatBlock block = Assert.Single(snapshot);
        Assert.Equal(FlatBlockKind.Html, block.Kind);
        Assert.Contains("hello", block.Inlines.Text);
        Assert.Contains("<div", block.Inlines.Text);
    }

    [Fact]
    public void ScriptTags_AreNeverInterpreted()
    {
        MarkdownSnapshot snapshot = TestDocument.Parse("<script>alert(1)</script>");

        FlatBlock block = Assert.Single(snapshot);
        Assert.Contains("<script>", block.Inlines.Text);
    }

    [Fact]
    public void LazyContinuation_KeepsParagraphTogether()
    {
        MarkdownSnapshot snapshot = TestDocument.Parse("> quoted\ncontinued");

        FlatBlock block = Assert.Single(snapshot);
        Assert.Equal(1, block.QuoteDepth);
        Assert.Equal("quoted\ncontinued", block.Inlines.Text);
    }

    [Fact]
    public void MalformedInput_DoesNotThrow()
    {
        string[] samples =
        {
            "```",
            "|||",
            "> > > >",
            "- - - - -",
            "[unclosed(",
            "![img](",
            "***bold*",
            "~~~~~~",
            "#".PadRight(200, '#'),
            "\t\t\t\t",
            "\u0000\u0001",
        };

        foreach (string sample in samples)
        {
            MarkdownSnapshot snapshot = TestDocument.Parse(sample);
            Assert.True(snapshot.Count >= 0);
        }
    }
}
