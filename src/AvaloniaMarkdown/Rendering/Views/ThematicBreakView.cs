using Avalonia;
using Avalonia.Media;
using AvaloniaMarkdown.Flattening;

namespace AvaloniaMarkdown.Rendering.Views;

/// <summary>Draws a horizontal rule.</summary>
public sealed class ThematicBreakView : MarkdownBlockView
{
    private const double RuleThickness = 1;
    private const double VerticalPadding = 8;

    protected override Size MeasureContent(Size availableSize) =>
        new(double.IsFinite(availableSize.Width) ? availableSize.Width : 0, RuleThickness + (VerticalPadding * 2));

    protected override void RenderContent(DrawingContext context, Rect contentRect)
    {
        context.FillRectangle(
            Theme.RuleBrush,
            new Rect(contentRect.X, contentRect.Y + VerticalPadding, contentRect.Width, RuleThickness));
    }
}
