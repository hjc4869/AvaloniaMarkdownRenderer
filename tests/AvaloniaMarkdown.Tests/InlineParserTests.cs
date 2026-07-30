using AvaloniaMarkdown.Ast;
using AvaloniaMarkdown.Flattening;
using Xunit;

namespace AvaloniaMarkdown.Tests;

public class InlineParserTests
{
    private static InlineContent Inlines(string markdown) => TestDocument.Parse(markdown)[0].Inlines;

    [Fact]
    public void Bold_Italic_And_BoldItalic()
    {
        Assert.Equal("bbbb", TestDocument.StyleMap(Inlines("**bold**")));
        Assert.Equal("iiiiii", TestDocument.StyleMap(Inlines("*italic*")));
        Assert.Equal("XXXX", TestDocument.StyleMap(Inlines("***both***")).Replace("both", "XXXX"));
    }

    [Fact]
    public void BoldItalic_CombinesBothStyles()
    {
        InlineContent content = Inlines("***both***");

        Assert.Equal("both", content.Text);
        InlineRun run = Assert.Single(content.Runs);
        Assert.True((run.Style & InlineStyle.Bold) != 0);
        Assert.True((run.Style & InlineStyle.Italic) != 0);
    }

    [Fact]
    public void NestedEmphasis_AppliesInnerAndOuter()
    {
        InlineContent content = Inlines("**bold *and italic* again**");

        Assert.Equal("bold and italic again", content.Text);
        Assert.Equal("bbbbbXXXXXXXXXXbbbbbb", TestDocument.StyleMap(content));
    }

    [Fact]
    public void Underscore_EmphasisInsideWord_IsLiteral()
    {
        InlineContent content = Inlines("snake_case_name");

        Assert.Equal("snake_case_name", content.Text);
        Assert.Equal(InlineStyle.None, Assert.Single(content.Runs).Style);
    }

    [Fact]
    public void Strikethrough_IsSupported()
    {
        InlineContent content = Inlines("~~gone~~");

        Assert.Equal("gone", content.Text);
        Assert.Equal(InlineStyle.Strikethrough, Assert.Single(content.Runs).Style);
    }

    [Fact]
    public void InlineCode_SuppressesMarkup()
    {
        InlineContent content = Inlines("`**not bold**`");

        Assert.Equal("**not bold**", content.Text);
        Assert.Equal(InlineStyle.Code, Assert.Single(content.Runs).Style);
    }

    [Fact]
    public void InlineCode_StripsOnePaddingSpace()
    {
        Assert.Equal("`", Inlines("`` ` ``").Text);
    }

    [Fact]
    public void EscapedCharacters_AreLiteral()
    {
        InlineContent content = Inlines(@"\*not emphasis\* and \\ and \[x\]");

        Assert.Equal(@"*not emphasis* and \ and [x]", content.Text);
        Assert.Equal(InlineStyle.None, Assert.Single(content.Runs).Style);
    }

    [Fact]
    public void Links_CaptureTextAndTarget()
    {
        InlineContent content = Inlines("see [the docs](https://example.com \"Title\") now");

        Assert.Equal("see the docs now", content.Text);
        InlineTarget target = Assert.Single(content.Targets);
        Assert.Equal("https://example.com", target.Url);
        Assert.Equal("Title", target.Title);
        Assert.Equal("....llllllll....", TestDocument.StyleMap(content));
    }

    [Fact]
    public void JavaScriptUrls_AreRejected()
    {
        InlineContent content = Inlines("[click](javascript:alert(1))");

        Assert.Empty(content.Targets);
        Assert.Contains("click", content.Text);
    }

    [Fact]
    public void AngleAutoLinks_AreDetected()
    {
        InlineContent content = Inlines("<https://example.com/a>");

        Assert.Equal("https://example.com/a", content.Text);
        Assert.Equal("https://example.com/a", Assert.Single(content.Targets).Url);
    }

    [Fact]
    public void BareUrls_AreDetected()
    {
        InlineContent content = Inlines("go to https://example.com/path?a=1 today.");

        InlineTarget target = Assert.Single(content.Targets);
        Assert.Equal("https://example.com/path?a=1", target.Url);
    }

    [Fact]
    public void WwwUrls_GetHttpsScheme()
    {
        Assert.Equal("https://www.example.com", Assert.Single(Inlines("www.example.com").Targets).Url);
    }

    [Fact]
    public void EmailAutoLink_GetsMailtoScheme()
    {
        Assert.Equal("mailto:a@b.com", Assert.Single(Inlines("<a@b.com>").Targets).Url);
    }

    [Fact]
    public void ImagesOnTheirOwnLine_BecomeImageBlocks()
    {
        MarkdownSnapshot snapshot = TestDocument.Parse("![alt text](https://example.com/a.png)");

        FlatBlock block = Assert.Single(snapshot);
        Assert.Equal(FlatBlockKind.Image, block.Kind);
        Assert.Equal("https://example.com/a.png", block.ImageUrl);
        Assert.Equal("alt text", block.ImageAlt);
    }

    [Fact]
    public void InlineImage_StaysInsideTheParagraph()
    {
        MarkdownSnapshot snapshot = TestDocument.Parse("before ![alt](https://example.com/a.png) after");

        FlatBlock block = Assert.Single(snapshot);
        Assert.Equal(FlatBlockKind.Paragraph, block.Kind);
        Assert.Equal("before alt after", block.Inlines.Text);
    }

    [Fact]
    public void SafeInlineHtml_MapsToStyles()
    {
        InlineContent content = Inlines("<b>bold</b> and <code>code</code>");

        Assert.Equal("bold and code", content.Text);
        Assert.Equal("bbbb.....cccc", TestDocument.StyleMap(content));
    }

    [Fact]
    public void UnknownInlineHtml_StaysLiteral()
    {
        Assert.Equal("<blink>x</blink>", Inlines("<blink>x</blink>").Text);
    }

    [Fact]
    public void HtmlEntities_AreDecoded()
    {
        Assert.Equal("< > & \u00a9 A", Inlines("&lt; &gt; &amp; &copy; &#65;").Text);
    }

    [Fact]
    public void SoftLineBreaks_BecomeHardBreaksByDefault()
    {
        Assert.Equal("one\ntwo", Inlines("one\ntwo").Text);
    }

    [Fact]
    public void SoftLineBreaks_CanBeSpaces()
    {
        var options = new MarkdownOptions { SoftLineBreaksAsHardBreaks = false };
        Assert.Equal("one two", TestDocument.Parse("one\ntwo", options)[0].Inlines.Text);
    }

    [Fact]
    public void UnmatchedDelimiters_RemainLiteralWhenClosed()
    {
        Assert.Equal("**unfinished", Inlines("**unfinished").Text);
    }

    [Fact]
    public void ExtremelyLongParagraph_ParsesInOneRun()
    {
        string text = new('x', 200_000);
        InlineContent content = Inlines(text);

        Assert.Equal(200_000, content.Text.Length);
        Assert.Single(content.Runs);
    }

    [Fact]
    public void ManyLinksInOneParagraph_DoNotDegrade()
    {
        string text = string.Join(' ', Enumerable.Range(0, 2000).Select(i => $"[l{i}](https://e.com/{i})"));
        InlineContent content = Inlines(text);

        Assert.Equal(2000, content.Targets.Length);
    }
}
