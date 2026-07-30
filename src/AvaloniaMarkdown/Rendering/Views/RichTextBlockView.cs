using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaMarkdown.Ast;
using AvaloniaMarkdown.Flattening;

namespace AvaloniaMarkdown.Rendering.Views;

/// <summary>
/// Renders paragraphs, headings and degraded HTML blocks as a single text layout.
/// </summary>
/// <remarks>
/// The layout is rebuilt only when the block version or the available width changes, so scrolling
/// over already-measured blocks performs no text shaping at all.
/// </remarks>
public sealed class RichTextBlockView : MarkdownBlockView
{
    private TextLayout? _layout;
    private double _layoutWidth = -1;
    private int _layoutVersion = -1;
    private InlineTarget? _hoveredTarget;

    public RichTextBlockView()
    {
        Cursor = null;
    }

    protected override void OnBlockChanged(FlatBlock? previous)
    {
        _layout = null;
        _layoutVersion = -1;
        _hoveredTarget = null;
    }

    protected override void OnDetached()
    {
        _layout = null;
        _layoutVersion = -1;
        _hoveredTarget = null;
        Cursor = null;
    }

    protected override Size MeasureContent(Size availableSize)
    {
        TextLayout layout = GetLayout(availableSize.Width);
        return new Size(layout.Width, layout.Height + BottomPadding);
    }

    protected override void RenderContent(DrawingContext context, Rect contentRect)
    {
        TextLayout layout = GetLayout(contentRect.Width);
        layout.Draw(context, contentRect.TopLeft);
    }

    private double BottomPadding => Block.Kind == FlatBlockKind.Heading ? 2 : 0;

    private TextLayout GetLayout(double width)
    {
        double constraint = double.IsFinite(width) && width > 0 ? width : double.PositiveInfinity;

        if (_layout is not null &&
            _layoutVersion == Block.Version &&
            Math.Abs(_layoutWidth - constraint) < 0.5)
        {
            return _layout;
        }

        MarkdownTheme theme = Theme;
        bool heading = Block.Kind == FlatBlockKind.Heading;

        double fontSize = heading ? theme.GetHeadingSize(Block.HeadingLevel) : theme.FontSize;
        IBrush foreground = Block.QuoteDepth > 0 ? theme.QuoteForeground : theme.Foreground;
        if (Block.Kind == FlatBlockKind.Html)
        {
            foreground = theme.MutedForeground;
        }

        _layout = InlineTextRenderer.CreateLayout(
            Block.Inlines,
            theme,
            fontSize,
            baseBold: heading,
            foreground,
            constraint);

        _layoutWidth = constraint;
        _layoutVersion = Block.Version;
        return _layout;
    }

    // ------------------------------------------------------------------
    // Link interaction
    // ------------------------------------------------------------------

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        InlineTarget? target = HitTest(e.GetPosition(this));
        if (ReferenceEquals(target, _hoveredTarget))
        {
            return;
        }

        _hoveredTarget = target;
        Cursor = target is null ? null : new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, target?.Title ?? target?.Url);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoveredTarget = null;
        Cursor = null;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        InlineTarget? target = HitTest(e.GetPosition(this));
        if (target is not null)
        {
            Host.OnTargetActivated(target);
            e.Handled = true;
        }
    }

    private InlineTarget? HitTest(Point point)
    {
        if (Block.Inlines.Targets.Length == 0 || _layout is null)
        {
            return null;
        }

        var local = new Point(point.X - ContentLeft, point.Y - TopSpacing);
        if (local.X < 0 || local.Y < 0)
        {
            return null;
        }

        return InlineTextRenderer.HitTestTarget(_layout, Block.Inlines, local);
    }
}
