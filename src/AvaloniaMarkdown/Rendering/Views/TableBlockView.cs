using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaMarkdown.Ast;
using AvaloniaMarkdown.Flattening;

namespace AvaloniaMarkdown.Rendering.Views;

/// <summary>
/// Renders a GFM pipe table with per-column alignment and horizontal scrolling.
/// </summary>
/// <remarks>
/// Cells are laid out as individual text layouts but drawn directly by this single control, so a
/// 20x10 table costs one visual instead of two hundred.
/// </remarks>
public sealed class TableBlockView : MarkdownBlockView
{
    private const double MinColumnWidth = 40;
    private const double MaxNaturalColumnWidth = 420;

    private TextLayout[]? _headerLayouts;
    private TextLayout[][]? _rowLayouts;
    private double[]? _columnWidths;
    private double[]? _rowHeights;
    private double _headerHeight;
    private double _totalWidth;
    private double _viewportWidth;
    private double _horizontalOffset;
    private int _layoutVersion = -1;
    private double _layoutConstraint = -1;

    protected override void OnBlockChanged(FlatBlock? previous)
    {
        _layoutVersion = -1;
        if (previous is null || previous.BlockId != Block.BlockId)
        {
            _horizontalOffset = 0;
        }
    }

    protected override void OnDetached()
    {
        _headerLayouts = null;
        _rowLayouts = null;
        _columnWidths = null;
        _rowHeights = null;
        _layoutVersion = -1;
        _horizontalOffset = 0;
    }

    protected override Size MeasureContent(Size availableSize)
    {
        double constraint = double.IsFinite(availableSize.Width) ? availableSize.Width : 800;
        BuildLayouts(constraint);

        _viewportWidth = constraint;

        double height = _headerHeight;
        if (_rowHeights is not null)
        {
            foreach (double rowHeight in _rowHeights)
            {
                height += rowHeight;
            }
        }

        return new Size(constraint, height + 1);
    }

    private void BuildLayouts(double constraint)
    {
        if (_layoutVersion == Block.Version && Math.Abs(_layoutConstraint - constraint) < 0.5)
        {
            return;
        }

        TableModel table = Block.Table!;
        MarkdownTheme theme = Theme;
        int columns = Math.Max(1, table.ColumnCount);
        double padding = theme.TableCellPadding;

        _headerLayouts = new TextLayout[columns];
        _rowLayouts = new TextLayout[table.Rows.Length][];
        _columnWidths = new double[columns];

        // Pass 1: natural widths.
        for (int c = 0; c < columns; c++)
        {
            InlineContent content = c < table.Header.Length ? table.Header[c] : InlineContent.Empty;
            TextLayout layout = InlineTextRenderer.CreateLayout(
                content, theme, theme.FontSize, baseBold: true, theme.Foreground, double.PositiveInfinity, TextAlignment.Left, TextWrapping.NoWrap);

            _headerLayouts[c] = layout;
            _columnWidths[c] = Math.Max(MinColumnWidth, Math.Min(MaxNaturalColumnWidth, layout.Width));
        }

        for (int r = 0; r < table.Rows.Length; r++)
        {
            InlineContent[] row = table.Rows[r];
            var layouts = new TextLayout[columns];
            for (int c = 0; c < columns; c++)
            {
                InlineContent content = c < row.Length ? row[c] : InlineContent.Empty;
                TextLayout layout = InlineTextRenderer.CreateLayout(
                    content, theme, theme.FontSize, baseBold: false, theme.Foreground, double.PositiveInfinity, TextAlignment.Left, TextWrapping.NoWrap);

                layouts[c] = layout;
                _columnWidths[c] = Math.Max(_columnWidths[c], Math.Min(MaxNaturalColumnWidth, layout.Width));
            }

            _rowLayouts[r] = layouts;
        }

        for (int c = 0; c < columns; c++)
        {
            _columnWidths[c] += padding * 2;
        }

        _totalWidth = _columnWidths.Sum();

        // Distribute slack so a narrow table still fills the available width.
        if (_totalWidth < constraint && columns > 0)
        {
            double extra = (constraint - _totalWidth) / columns;
            for (int c = 0; c < columns; c++)
            {
                _columnWidths[c] += extra;
            }

            _totalWidth = constraint;
        }

        double rowPadding = padding;
        _headerHeight = (_headerLayouts.Length == 0 ? theme.FontSize : _headerLayouts.Max(l => l.Height)) + (rowPadding * 2);

        _rowHeights = new double[table.Rows.Length];
        for (int r = 0; r < table.Rows.Length; r++)
        {
            _rowHeights[r] = _rowLayouts[r].Max(l => l.Height) + (rowPadding * 2);
        }

        _layoutVersion = Block.Version;
        _layoutConstraint = constraint;
    }

    protected override void RenderContent(DrawingContext context, Rect contentRect)
    {
        BuildLayouts(contentRect.Width);

        if (_columnWidths is null || _headerLayouts is null || _rowLayouts is null || _rowHeights is null)
        {
            return;
        }

        MarkdownTheme theme = Theme;
        TableModel table = Block.Table!;
        var pen = new Pen(theme.TableBorder, 1);
        double padding = theme.TableCellPadding;

        using (context.PushClip(contentRect))
        {
            double originX = contentRect.X - _horizontalOffset;

            // Header background.
            context.FillRectangle(theme.TableHeaderBackground, new Rect(originX, contentRect.Y, _totalWidth, _headerHeight));

            double y = contentRect.Y;
            DrawRow(context, _headerLayouts, table, originX, y, _headerHeight, padding);
            y += _headerHeight;

            for (int r = 0; r < _rowLayouts.Length; r++)
            {
                DrawRow(context, _rowLayouts[r], table, originX, y, _rowHeights[r], padding);
                y += _rowHeights[r];
            }

            // Grid lines.
            double bottom = y;
            double x = originX;
            for (int c = 0; c <= _columnWidths.Length; c++)
            {
                context.DrawLine(pen, new Point(x, contentRect.Y), new Point(x, bottom));
                if (c < _columnWidths.Length)
                {
                    x += _columnWidths[c];
                }
            }

            y = contentRect.Y;
            context.DrawLine(pen, new Point(originX, y), new Point(originX + _totalWidth, y));
            y += _headerHeight;
            context.DrawLine(pen, new Point(originX, y), new Point(originX + _totalWidth, y));

            for (int r = 0; r < _rowHeights.Length; r++)
            {
                y += _rowHeights[r];
                context.DrawLine(pen, new Point(originX, y), new Point(originX + _totalWidth, y));
            }
        }
    }

    private void DrawRow(DrawingContext context, TextLayout[] layouts, TableModel table, double originX, double y, double rowHeight, double padding)
    {
        double x = originX;
        for (int c = 0; c < layouts.Length; c++)
        {
            double columnWidth = _columnWidths![c];
            TextLayout layout = layouts[c];

            TableAlignment alignment = c < table.Alignments.Length ? table.Alignments[c] : TableAlignment.None;
            double cellX = alignment switch
            {
                TableAlignment.Right => x + columnWidth - padding - layout.Width,
                TableAlignment.Center => x + ((columnWidth - layout.Width) / 2),
                _ => x + padding,
            };

            layout.Draw(context, new Point(cellX, y + padding));
            x += columnWidth;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        double delta = e.Delta.X;
        if (delta == 0 && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            delta = e.Delta.Y;
        }

        if (delta == 0)
        {
            return;
        }

        double max = Math.Max(0, _totalWidth - _viewportWidth);
        double next = Math.Clamp(_horizontalOffset - (delta * 40), 0, max);

        if (Math.Abs(next - _horizontalOffset) > 0.01)
        {
            _horizontalOffset = next;
            InvalidateVisual();
            e.Handled = true;
        }
    }
}
