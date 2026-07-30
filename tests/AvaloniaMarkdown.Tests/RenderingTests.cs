using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaMarkdown.Ast;
using AvaloniaMarkdown.Flattening;
using AvaloniaMarkdown.Images;
using AvaloniaMarkdown.Rendering;
using AvaloniaMarkdown.Rendering.Views;
using Xunit;

namespace AvaloniaMarkdown.Tests;

public class RenderingTests
{
    private const int WindowWidth = 800;
    private const int WindowHeight = 600;

    private static (Window Window, MarkdownView View, MarkdownDocument Document) CreateHost(string markdown, bool complete = true)
    {
        MarkdownDocument document = TestDocument.Create();
        document.Append(markdown);
        if (complete)
        {
            document.Complete();
        }

        var view = new MarkdownView
        {
            Document = document,
            MarkdownTheme = MarkdownTheme.Light,
            MinimumUpdateInterval = TimeSpan.Zero,
        };
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = view };

        window.Show();
        Pump(window);

        return (window, view, document);
    }

    private static void Pump(Window window)
    {
        for (int i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaFact]
    public void RendersBlocksIntoTheVisualTree()
    {
        (Window window, MarkdownView view, _) = CreateHost("# Title\n\nSome paragraph text.\n\n---\n");

        Assert.Equal(3, view.Panel.Snapshot.Count);
        Assert.Equal(3, view.Panel.RealizedCount);
        Assert.Contains(view.Panel.Children, c => c is RichTextBlockView);
        Assert.Contains(view.Panel.Children, c => c is ThematicBreakView);

        window.Close();
    }

    [AvaloniaFact]
    public void EachBlockKindGetsItsOwnViewType()
    {
        (Window window, MarkdownView view, _) = CreateHost(
            "para\n\n```\ncode\n```\n\n| a |\n|---|\n| b |\n\n---\n");

        Assert.Contains(view.Panel.Children, c => c is RichTextBlockView);
        Assert.Contains(view.Panel.Children, c => c is CodeSegmentView);
        Assert.Contains(view.Panel.Children, c => c is TableBlockView);
        Assert.Contains(view.Panel.Children, c => c is ThematicBreakView);

        window.Close();
    }

    /// <summary>
    /// The whole point of virtualization: a 20 000 block document must materialise only the
    /// blocks that intersect the viewport plus the overscan region.
    /// </summary>
    [AvaloniaFact]
    public void LargeDocument_OnlyMaterialisesTheViewport()
    {
        string markdown = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"Paragraph number {i}.\n\n"));
        (Window window, MarkdownView view, _) = CreateHost(markdown);

        Assert.Equal(20_000, view.Panel.Snapshot.Count);
        Assert.InRange(view.Panel.RealizedCount, 1, 200);
        Assert.Equal(view.Panel.RealizedCount, view.Panel.Children.Count);

        window.Close();
    }

    [AvaloniaFact]
    public void Scrolling_RealisesADifferentWindowOfBlocks()
    {
        string markdown = string.Concat(Enumerable.Range(0, 5_000).Select(i => $"Paragraph number {i}.\n\n"));
        (Window window, MarkdownView view, _) = CreateHost(markdown);

        int firstBefore = view.Panel.FirstRealizedIndex;
        int realizedBefore = view.Panel.RealizedCount;

        view.Panel.ScrollToBlock(2_500);
        Pump(window);

        Assert.NotEqual(firstBefore, view.Panel.FirstRealizedIndex);
        Assert.InRange(view.Panel.FirstRealizedIndex, 2_400, 2_600);
        Assert.InRange(view.Panel.RealizedCount, 1, realizedBefore * 3);

        window.Close();
    }

    [AvaloniaFact]
    public void ScrollExtent_GrowsWithTheDocument()
    {
        (Window window, MarkdownView view, MarkdownDocument document) =
            CreateHost(string.Concat(Enumerable.Repeat("line\n\n", 100)), complete: false);

        double before = view.Panel.Extent.Height;

        document.Append(string.Concat(Enumerable.Repeat("line\n\n", 100)));
        Pump(window);

        Assert.True(view.Panel.Extent.Height > before);

        window.Close();
    }

    /// <summary>
    /// Streaming a token into an already-realised block must update that control in place; if the
    /// control were recreated the UI would flicker and lose focus/selection.
    /// </summary>
    [AvaloniaFact]
    public void StreamingAToken_ReusesTheExistingControl()
    {
        (Window window, MarkdownView view, MarkdownDocument document) = CreateHost("Hello", complete: false);

        Control control = Assert.Single(view.Panel.Children);

        for (int i = 0; i < 20; i++)
        {
            document.Append(" more");
            Pump(window);
            Assert.Same(control, Assert.Single(view.Panel.Children));
        }

        var textView = Assert.IsType<RichTextBlockView>(control);
        Assert.Equal("Hello" + string.Concat(Enumerable.Repeat(" more", 20)), textView.Block.Inlines.Text);

        window.Close();
    }

    [AvaloniaFact]
    public void ControlsAreRecycledRatherThanReallocated()
    {
        string markdown = string.Concat(Enumerable.Range(0, 2_000).Select(i => $"Paragraph {i}.\n\n"));
        (Window window, MarkdownView view, _) = CreateHost(markdown);

        var seen = new HashSet<Control>();
        for (int block = 0; block < 2_000; block += 40)
        {
            view.Panel.ScrollToBlock(block);
            Pump(window);
            foreach (Control child in view.Panel.Children)
            {
                seen.Add(child);
            }
        }

        // Without recycling this would approach the number of blocks visited.
        Assert.InRange(seen.Count, 1, 200);

        window.Close();
    }

    [AvaloniaFact]
    public void LinkClick_RaisesTheEvent()
    {
        (Window window, MarkdownView view, _) = CreateHost("[click me](https://example.com)\n");

        MarkdownLinkEventArgs? captured = null;
        view.LinkClicked += (_, e) =>
        {
            captured = e;
            e.Handled = true;
        };

        var textView = Assert.IsType<RichTextBlockView>(Assert.Single(view.Panel.Children));
        InlineTarget target = Assert.Single(textView.Block.Inlines.Targets);
        ((IMarkdownHost)view).OnTargetActivated(target);

        Assert.NotNull(captured);
        Assert.Equal("https://example.com", captured!.Url);

        window.Close();
    }

    /// <summary>
    /// Regression: the ScrollChanged notification for an auto-scroll arrives after the scroll call
    /// returned, so it used to be mistaken for user scrolling. Every further update was then
    /// deferred and the view froze on the first screenful of text.
    /// </summary>
    [AvaloniaFact]
    public void Streaming_KeepsUpdatingAfterTheDocumentOutgrowsTheViewport()
    {
        MarkdownDocument document = TestDocument.Create();
        var view = new MarkdownView
        {
            Document = document,
            MarkdownTheme = MarkdownTheme.Light,
            MinimumUpdateInterval = TimeSpan.Zero,
        };
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = view };

        window.Show();
        Pump(window);

        for (int i = 0; i < 200; i++)
        {
            document.Append($"Paragraph number {i}.\n\n");
            Pump(window);
        }

        Assert.Equal(document.Snapshot.Count, view.Panel.Snapshot.Count);
        Assert.True(
            view.Panel.LastRealizedIndexExclusive >= view.Panel.Snapshot.Count - 1,
            $"the tail is not realised: last={view.Panel.LastRealizedIndexExclusive} count={view.Panel.Snapshot.Count}");

        window.Close();
    }

    /// <summary>
    /// Programmatic scrolling must be pushed to the enclosing ScrollViewer, otherwise the
    /// presenter keeps a stale offset and shoves it back on the next user scroll.
    /// </summary>
    [AvaloniaFact]
    public void ProgrammaticScroll_StaysInSyncWithTheScrollViewer()
    {
        string markdown = string.Concat(Enumerable.Range(0, 500).Select(i => $"Paragraph number {i}.\n\n"));
        (Window window, MarkdownView view, _) = CreateHost(markdown);

        view.Panel.ScrollToBlock(250);
        Pump(window);

        Assert.Equal(view.Panel.Offset.Y, view.ScrollViewer.Offset.Y, 1);

        window.Close();
    }

    [AvaloniaFact]
    public void ThemeChange_RebuildsWithoutLosingContent()
    {
        (Window window, MarkdownView view, _) = CreateHost("# Title\n\nBody\n");

        view.MarkdownTheme = MarkdownTheme.Dark;
        Pump(window);

        Assert.Equal(2, view.Panel.RealizedCount);
        Assert.All(view.Panel.Children, c => Assert.IsType<RichTextBlockView>(c));

        window.Close();
    }

    [AvaloniaFact]
    public void BuiltInThemes_CarryAPageBackground()
    {
        Assert.NotNull(MarkdownTheme.Light.Background);
        Assert.NotNull(MarkdownTheme.Dark.Background);
        Assert.NotEqual(MarkdownTheme.Light.Background, MarkdownTheme.Dark.Background);
    }

    /// <summary>The page background must actually reach the framebuffer, not just the theme object.</summary>
    [AvaloniaFact]
    public void ThemeBackground_IsPaintedAndFollowsTheTheme()
    {
        (Window window, MarkdownView view, _) = CreateHost("# Title\n\nBody text.\n");

        view.MarkdownTheme = MarkdownTheme.Dark;
        Pump(window);

        uint dark = SamplePixel(window);

        view.MarkdownTheme = MarkdownTheme.Light;
        Pump(window);

        uint light = SamplePixel(window);

        Assert.NotEqual(dark, light);
        Assert.True(Luminance(light) > Luminance(dark), $"light={light:X8} dark={dark:X8}");

        window.Close();
    }

    /// <summary>Reads one pixel from a region the document never covers (below the last block).</summary>
    private static uint SamplePixel(Window window)
    {
        Avalonia.Media.Imaging.WriteableBitmap frame =
            window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame captured");

        using Avalonia.Platform.ILockedFramebuffer buffer = frame.Lock();
        nint address = buffer.Address + (buffer.RowBytes * (WindowHeight - 40)) + (4 * (WindowWidth / 2));
        return (uint)System.Runtime.InteropServices.Marshal.ReadInt32(address);
    }

    private static int Luminance(uint pixel) =>
        (int)((pixel & 0xFF) + ((pixel >> 8) & 0xFF) + ((pixel >> 16) & 0xFF));

    [AvaloniaFact]
    public void CodeBlock_ExposesFullTextForCopy()
    {
        (Window window, MarkdownView view, _) = CreateHost("```\nalpha\nbeta\ngamma\n```\n");

        var code = Assert.IsType<CodeSegmentView>(Assert.Single(view.Panel.Children));
        string text = ((IMarkdownHost)view).GetCodeBlockText(code.Block.BlockId);

        Assert.Equal("alpha\nbeta\ngamma", text);

        window.Close();
    }

    [AvaloniaFact]
    public void ImageBlock_RendersAPlaceholderThenTheBitmap()
    {
        const string OnePixelPng =
            "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

        (Window window, MarkdownView view, _) = CreateHost($"![dot]({OnePixelPng})\n");

        var image = Assert.IsType<ImageBlockView>(Assert.Single(view.Panel.Children));
        Assert.Equal(FlatBlockKind.Image, image.Block.Kind);
        Assert.True(image.Bounds.Height > 0);

        window.Close();
    }

    [AvaloniaFact]
    public async Task ImageCache_DecodesDataUrisAndDeduplicates()
    {
        const string OnePixelPng =
            "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

        using var cache = new MarkdownImageCache();

        Bitmap? first = await cache.GetAsync(OnePixelPng, 256, CancellationToken.None);
        Bitmap? second = await cache.GetAsync(OnePixelPng, 256, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, first!.PixelSize.Width);
    }

    [AvaloniaFact]
    public async Task ImageCache_RejectsUnsafeSchemes()
    {
        using var cache = new MarkdownImageCache();

        Assert.Null(await cache.GetAsync("javascript:alert(1)", 256, CancellationToken.None));
        Assert.Null(await cache.GetAsync("vbscript:msgbox", 256, CancellationToken.None));
    }

    [AvaloniaFact]
    public void AutoScrollToEnd_FollowsTheStream()
    {
        (Window window, MarkdownView view, MarkdownDocument document) =
            CreateHost(string.Concat(Enumerable.Repeat("line\n\n", 200)), complete: false);

        view.ScrollToEnd();
        Pump(window);
        Assert.True(view.Panel.IsScrolledToEnd);

        document.Append(string.Concat(Enumerable.Repeat("more\n\n", 200)));
        Pump(window);

        Assert.True(view.Panel.IsScrolledToEnd);

        window.Close();
    }
}
