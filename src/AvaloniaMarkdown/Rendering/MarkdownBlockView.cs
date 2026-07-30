using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using AvaloniaMarkdown.Ast;
using AvaloniaMarkdown.Flattening;

namespace AvaloniaMarkdown.Rendering;

/// <summary>
/// Services a block view needs from the control that hosts it.
/// </summary>
public interface IMarkdownHost
{
    MarkdownTheme Theme { get; }

    /// <summary>Invoked when the user activates a link or image.</summary>
    void OnTargetActivated(InlineTarget target);

    /// <summary>Returns the complete source text of a code block spanning several segments.</summary>
    string GetCodeBlockText(int blockId);

    /// <summary>Copies text to the clipboard, if one is available.</summary>
    void CopyToClipboard(string text);

    /// <summary>Loads (or returns a cached) bitmap for an image block.</summary>
    Task<Bitmap?> LoadImageAsync(string url, int decodeWidth, CancellationToken cancellationToken);

    /// <summary>Requests a re-layout because a block's intrinsic size changed asynchronously.</summary>
    void InvalidateBlockMeasure(MarkdownBlockView view);

    /// <summary>Redraws every realised view belonging to <paramref name="blockId"/>.</summary>
    void InvalidateBlock(int blockId);
}

/// <summary>
/// Base class for every block view.
/// </summary>
/// <remarks>
/// <para>
/// A block view owns exactly one <see cref="FlatBlock"/> and is recycled by
/// <see cref="MarkdownVirtualizingPanel"/> when it scrolls out of view. Shared chrome —
/// block-quote bars, list markers, task checkboxes and indentation — is drawn here so that
/// concrete views only deal with their own content.
/// </para>
/// <para>
/// <see cref="UpdateBlock"/> is the in-place update path used by the diff engine's
/// <c>UpdateInline</c> operation: the control instance survives, only its content changes, which
/// is what makes streaming free of flicker.
/// </para>
/// </remarks>
public abstract class MarkdownBlockView : Control
{
    private TextLayout? _markerLayout;
    private double _topSpacing;

    protected MarkdownBlockView()
    {
        ClipToBounds = true;
    }

    /// <summary>The block currently displayed. Never null once attached.</summary>
    public FlatBlock Block { get; private set; } = null!;

    public IMarkdownHost Host { get; private set; } = null!;

    /// <summary>Colours, fonts and metrics in use. Hides the unrelated <see cref="StyledElement.Theme"/>.</summary>
    protected new MarkdownTheme Theme => Host.Theme;
    /// <summary>Horizontal offset of the content area, from quotes and list nesting.</summary>
    protected double ContentLeft =>
        (Block.QuoteDepth * Theme.QuoteIndent) + (Block.IndentLevel * Theme.ListIndent);

    /// <summary>Vertical gap inserted above this block.</summary>
    protected double TopSpacing => _topSpacing;

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    internal void Attach(FlatBlock block, IMarkdownHost host)
    {
        Host = host;
        Block = block;
        _markerLayout = null;
        _topSpacing = ComputeTopSpacing(block);
        OnBlockChanged(null);
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Applies new content to a live control without recreating it.</summary>
    internal void UpdateBlock(FlatBlock block)
    {
        FlatBlock previous = Block;
        Block = block;
        _markerLayout = null;
        _topSpacing = ComputeTopSpacing(block);
        OnBlockChanged(previous);
        InvalidateMeasure();
        InvalidateVisual();
    }

    internal void Detach()
    {
        OnDetached();
        _markerLayout = null;
    }

    /// <summary>Called after <see cref="Block"/> changes.</summary>
    protected virtual void OnBlockChanged(FlatBlock? previous)
    {
    }

    /// <summary>Called before the view returns to the recycle pool.</summary>
    protected virtual void OnDetached()
    {
    }

    private double ComputeTopSpacing(FlatBlock block) =>
        block.IndentLevel > 0 && block.IsTightList ? Theme.TightBlockSpacing : Theme.BlockSpacing;

    // ------------------------------------------------------------------
    // Layout
    // ------------------------------------------------------------------

    protected sealed override Size MeasureOverride(Size availableSize)
    {
        double left = ContentLeft;
        double width = Math.Max(0, availableSize.Width - left);
        Size content = MeasureContent(new Size(width, double.PositiveInfinity));

        double totalWidth = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : left + content.Width;

        return new Size(totalWidth, content.Height + _topSpacing);
    }

    protected sealed override Size ArrangeOverride(Size finalSize)
    {
        double left = ContentLeft;
        ArrangeContent(new Rect(
            left,
            _topSpacing,
            Math.Max(0, finalSize.Width - left),
            Math.Max(0, finalSize.Height - _topSpacing)));

        return finalSize;
    }

    /// <summary>Measures the content area (already excluding indentation and spacing).</summary>
    protected abstract Size MeasureContent(Size availableSize);

    /// <summary>Arranges child visuals inside the content rectangle.</summary>
    protected virtual void ArrangeContent(Rect contentRect)
    {
    }

    /// <summary>Draws the content area.</summary>
    protected virtual void RenderContent(DrawingContext context, Rect contentRect)
    {
    }

    // ------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------

    public sealed override void Render(DrawingContext context)
    {
        MarkdownTheme theme = Theme;
        double height = Bounds.Height;

        // Block-quote bars span the full height of every block they contain.
        for (int depth = 0; depth < Block.QuoteDepth; depth++)
        {
            double x = depth * theme.QuoteIndent;
            context.FillRectangle(theme.QuoteBar, new Rect(x, 0, theme.QuoteBarWidth, height));
        }

        double left = ContentLeft;
        var contentRect = new Rect(left, _topSpacing, Math.Max(0, Bounds.Width - left), Math.Max(0, height - _topSpacing));

        RenderMarker(context, contentRect);
        RenderContent(context, contentRect);
    }

    private void RenderMarker(DrawingContext context, Rect contentRect)
    {
        MarkdownTheme theme = Theme;

        if (Block.TaskChecked is { } isChecked)
        {
            const double Size = 13;
            double boxLeft = contentRect.X - Size - 6;
            double boxTop = contentRect.Y + Math.Max(0, (theme.FontSize * 1.35 - Size) / 2);
            var box = new Rect(boxLeft, boxTop, Size, Size);

            var pen = new Pen(theme.MarkerForeground, 1.2);
            context.DrawRectangle(isChecked ? theme.LinkForeground : null, pen, box, 3, 3);

            if (isChecked)
            {
                var check = new Pen(Brushes.White, 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                context.DrawLine(check, new Point(box.X + 3, box.Y + 6.5), new Point(box.X + 5.5, box.Y + 9.5));
                context.DrawLine(check, new Point(box.X + 5.5, box.Y + 9.5), new Point(box.X + 10, box.Y + 3.5));
            }

            return;
        }

        if (Block.Marker is not { Length: > 0 } marker)
        {
            return;
        }

        _markerLayout ??= new TextLayout(
            marker,
            theme.GetTypeface(bold: false, italic: false, monospace: false),
            theme.FontSize,
            theme.MarkerForeground);

        double x = contentRect.X - _markerLayout.Width - 8;
        _markerLayout.Draw(context, new Point(Math.Max(0, x), contentRect.Y));
    }
}
