using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaMarkdown.Flattening;

namespace AvaloniaMarkdown.Rendering.Views;

/// <summary>
/// Renders one virtualised segment of a fenced or indented code block.
/// </summary>
/// <remarks>
/// <para>
/// Large code blocks are split into fixed-size line segments by the flattener so that a
/// 10 000 line listing is laid out one screenful at a time. All segments of a block share a
/// <see cref="CodeBlockState"/>, which carries the horizontal scroll offset and the selection so
/// the block still behaves as a single unit.
/// </para>
/// <para>
/// Whitespace is preserved exactly: the text is laid out with <see cref="TextWrapping.NoWrap"/>
/// in the monospace typeface, and horizontal overflow is scrolled rather than wrapped.
/// </para>
/// </remarks>
public sealed class CodeSegmentView : MarkdownBlockView
{
    private const double ScrollBarHeight = 6;
    private const double CopyButtonWidth = 46;

    private readonly CodeBlockState _fallbackState = new();

    private TextLayout? _layout;
    private TextLayout? _gutterLayout;
    private TextLayout? _headerLayout;
    private TextLayout? _copyLayout;
    private int _layoutVersion = -1;

    private double _lineHeight = 16;
    private double _charWidth = 8;
    private double _gutterWidth;
    private double _headerHeight;
    private double _viewportWidth;
    private bool _selecting;
    private bool _copyHot;

    public CodeSegmentView()
    {
        Focusable = true;
    }

    private CodeBlockState State => Block.CodeState ?? _fallbackState;

    private bool HasHeader =>
        Block.SegmentRole is CodeSegmentRole.First or CodeSegmentRole.Only &&
        Theme.ShowCodeLanguageLabel;

    private bool HasScrollBar =>
        Block.SegmentRole is CodeSegmentRole.Last or CodeSegmentRole.Only &&
        State.MaxLineWidth > _viewportWidth + 0.5;

    protected override void OnBlockChanged(FlatBlock? previous)
    {
        if (previous is null || previous.Version != Block.Version || previous.SegmentIndex != Block.SegmentIndex)
        {
            _layout = null;
            _gutterLayout = null;
            _layoutVersion = -1;
        }

        _headerLayout = null;
    }

    protected override void OnDetached()
    {
        _layout = null;
        _gutterLayout = null;
        _headerLayout = null;
        _layoutVersion = -1;
        _selecting = false;
    }

    // ------------------------------------------------------------------
    // Layout
    // ------------------------------------------------------------------

    protected override Size MeasureContent(Size availableSize)
    {
        EnsureLayout();

        _headerHeight = HasHeader ? Math.Ceiling(Theme.CodeFontSize * 1.9) : 0;
        double padding = Theme.CodePadding;

        double top = Block.SegmentRole is CodeSegmentRole.First or CodeSegmentRole.Only ? padding : 0;
        double bottom = Block.SegmentRole is CodeSegmentRole.Last or CodeSegmentRole.Only ? padding : 0;

        double height = _headerHeight + top + (_lineHeight * Math.Max(1, Block.LineCount)) + bottom;
        if (HasScrollBar)
        {
            height += ScrollBarHeight + 2;
        }

        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : _gutterWidth + (_layout?.Width ?? 0);
        _viewportWidth = Math.Max(0, width - (padding * 2) - _gutterWidth);

        return new Size(width, height);
    }

    private void EnsureLayout()
    {
        if (_layout is not null && _layoutVersion == Block.Version)
        {
            return;
        }

        MarkdownTheme theme = Theme;
        Typeface typeface = theme.GetTypeface(bold: false, italic: false, monospace: true);

        _layout = new TextLayout(
            Block.CodeText ?? string.Empty,
            typeface,
            theme.CodeFontSize,
            theme.CodeForeground,
            TextAlignment.Left,
            TextWrapping.NoWrap);

        _lineHeight = _layout.TextLines.Count > 0 ? _layout.TextLines[0].Height : theme.CodeFontSize * 1.4;

        // Monospace: one measurement is enough to map columns to pixels.
        var probe = new TextLayout(new string('0', 32), typeface, theme.CodeFontSize, theme.CodeForeground, TextAlignment.Left, TextWrapping.NoWrap);
        _charWidth = probe.Width / 32;

        _gutterWidth = 0;
        _gutterLayout = null;

        if (theme.ShowCodeLineNumbers)
        {
            int lastNumber = Block.FirstLineNumber + Math.Max(1, Block.LineCount) - 1;
            int digits = lastNumber.ToString().Length;
            _gutterWidth = (_charWidth * digits) + 14;
            _gutterLayout = BuildGutterLayout(typeface, digits);
        }

        State.MaxLineWidth = Math.Max(State.MaxLineWidth, _layout.Width);
        _layoutVersion = Block.Version;
    }

    private TextLayout BuildGutterLayout(Typeface typeface, int digits)
    {
        var builder = new System.Text.StringBuilder(Block.LineCount * (digits + 1));
        for (int i = 0; i < Math.Max(1, Block.LineCount); i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append((Block.FirstLineNumber + i).ToString().PadLeft(digits));
        }

        return new TextLayout(
            builder.ToString(),
            typeface,
            Theme.CodeFontSize,
            Theme.MutedForeground,
            TextAlignment.Left,
            TextWrapping.NoWrap);
    }

    // ------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------

    protected override void RenderContent(DrawingContext context, Rect contentRect)
    {
        EnsureLayout();

        MarkdownTheme theme = Theme;
        double padding = theme.CodePadding;
        bool first = Block.SegmentRole is CodeSegmentRole.First or CodeSegmentRole.Only;
        bool last = Block.SegmentRole is CodeSegmentRole.Last or CodeSegmentRole.Only;

        double radius = 6;
        var background = new Rect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height);

        // Rounded only at the outer edges so consecutive segments look like one block.
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            BuildBlockPath(ctx, background, first ? radius : 0, last ? radius : 0);
        }

        context.DrawGeometry(theme.CodeBackground, new Pen(theme.CodeBorder, 1), geometry);

        double textTop = contentRect.Y + _headerHeight + (first ? padding : 0);
        double textLeft = contentRect.X + padding + _gutterWidth;

        if (first)
        {
            RenderHeader(context, contentRect);
        }

        if (_gutterLayout is not null)
        {
            _gutterLayout.Draw(context, new Point(contentRect.X + padding - 4, textTop));
        }

        var clip = new Rect(textLeft, textTop, Math.Max(0, contentRect.Right - padding - textLeft), _lineHeight * Math.Max(1, Block.LineCount));
        using (context.PushClip(clip))
        {
            double offset = -State.HorizontalOffset;
            RenderSelection(context, new Point(textLeft + offset, textTop));
            _layout!.Draw(context, new Point(textLeft + offset, textTop));
        }

        if (HasScrollBar)
        {
            RenderScrollBar(context, contentRect);
        }
    }

    private static void BuildBlockPath(StreamGeometryContext ctx, Rect rect, double topRadius, double bottomRadius)
    {
        ctx.BeginFigure(new Point(rect.X + topRadius, rect.Y), isFilled: true);
        ctx.LineTo(new Point(rect.Right - topRadius, rect.Y));
        if (topRadius > 0)
        {
            ctx.ArcTo(new Point(rect.Right, rect.Y + topRadius), new Size(topRadius, topRadius), 0, false, SweepDirection.Clockwise);
        }

        ctx.LineTo(new Point(rect.Right, rect.Bottom - bottomRadius));
        if (bottomRadius > 0)
        {
            ctx.ArcTo(new Point(rect.Right - bottomRadius, rect.Bottom), new Size(bottomRadius, bottomRadius), 0, false, SweepDirection.Clockwise);
        }

        ctx.LineTo(new Point(rect.X + bottomRadius, rect.Bottom));
        if (bottomRadius > 0)
        {
            ctx.ArcTo(new Point(rect.X, rect.Bottom - bottomRadius), new Size(bottomRadius, bottomRadius), 0, false, SweepDirection.Clockwise);
        }

        ctx.LineTo(new Point(rect.X, rect.Y + topRadius));
        if (topRadius > 0)
        {
            ctx.ArcTo(new Point(rect.X + topRadius, rect.Y), new Size(topRadius, topRadius), 0, false, SweepDirection.Clockwise);
        }

        ctx.EndFigure(true);
    }

    private void RenderHeader(DrawingContext context, Rect contentRect)
    {
        MarkdownTheme theme = Theme;
        var headerRect = new Rect(contentRect.X + 1, contentRect.Y + 1, Math.Max(0, contentRect.Width - 2), Math.Max(0, _headerHeight - 1));

        context.FillRectangle(theme.TableHeaderBackground, headerRect);
        context.DrawLine(
            new Pen(theme.CodeBorder, 1),
            new Point(headerRect.X, headerRect.Bottom),
            new Point(headerRect.Right, headerRect.Bottom));

        Typeface uiTypeface = theme.GetTypeface(bold: false, italic: false, monospace: false);
        double labelSize = theme.CodeFontSize * 0.92;

        _headerLayout ??= new TextLayout(
            string.IsNullOrEmpty(Block.Language) ? "text" : Block.Language,
            uiTypeface,
            labelSize,
            theme.MutedForeground);

        _copyLayout ??= new TextLayout("Copy", uiTypeface, labelSize, theme.MutedForeground);

        double centerY = headerRect.Y + ((headerRect.Height - _headerLayout.Height) / 2);
        _headerLayout.Draw(context, new Point(headerRect.X + theme.CodePadding, centerY));

        var copyRect = CopyButtonRect(contentRect);
        if (_copyHot)
        {
            context.FillRectangle(theme.InlineCodeBackground, copyRect, 4f);
        }

        _copyLayout.Draw(context, new Point(
            copyRect.X + ((copyRect.Width - _copyLayout.Width) / 2),
            headerRect.Y + ((headerRect.Height - _copyLayout.Height) / 2)));
    }

    private Rect CopyButtonRect(Rect contentRect) =>
        new(contentRect.Right - CopyButtonWidth - Theme.CodePadding, contentRect.Y + 3, CopyButtonWidth, Math.Max(0, _headerHeight - 6));

    private void RenderSelection(DrawingContext context, Point origin)
    {
        CodeBlockState state = State;
        if (!state.HasSelection)
        {
            return;
        }

        (int startLine, int startColumn, int endLine, int endColumn) = NormalizedSelection(state);

        int segmentFirst = Block.FirstLineNumber - 1;
        int segmentLast = segmentFirst + Math.Max(1, Block.LineCount) - 1;

        for (int line = Math.Max(startLine, segmentFirst); line <= Math.Min(endLine, segmentLast); line++)
        {
            int from = line == startLine ? startColumn : 0;
            int to = line == endLine ? endColumn : LineLength(line - segmentFirst) + 1;

            if (to <= from)
            {
                continue;
            }

            var rect = new Rect(
                origin.X + (from * _charWidth),
                origin.Y + ((line - segmentFirst) * _lineHeight),
                (to - from) * _charWidth,
                _lineHeight);

            context.FillRectangle(Theme.SelectionBrush, rect);
        }
    }

    private void RenderScrollBar(DrawingContext context, Rect contentRect)
    {
        double trackWidth = Math.Max(0, contentRect.Width - (Theme.CodePadding * 2));
        if (trackWidth <= 0 || State.MaxLineWidth <= 0)
        {
            return;
        }

        double ratio = Math.Min(1, _viewportWidth / State.MaxLineWidth);
        double thumbWidth = Math.Max(24, trackWidth * ratio);
        double maxOffset = Math.Max(1, State.MaxLineWidth - _viewportWidth);
        double thumbX = contentRect.X + Theme.CodePadding + ((trackWidth - thumbWidth) * (State.HorizontalOffset / maxOffset));

        var rect = new Rect(thumbX, contentRect.Bottom - ScrollBarHeight - 3, thumbWidth, ScrollBarHeight);
        context.FillRectangle(Theme.QuoteBar, rect, (float)(ScrollBarHeight / 2));
    }

    // ------------------------------------------------------------------
    // Interaction
    // ------------------------------------------------------------------

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

        double max = Math.Max(0, State.MaxLineWidth - _viewportWidth);
        double next = Math.Clamp(State.HorizontalOffset - (delta * _charWidth * 3), 0, max);

        if (Math.Abs(next - State.HorizontalOffset) > 0.01)
        {
            State.HorizontalOffset = next;
            Host.InvalidateBlock(Block.BlockId);
            e.Handled = true;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        Point position = e.GetPosition(this);
        var contentRect = ContentRect();

        if (HasHeader && CopyButtonRect(contentRect).Contains(position))
        {
            Host.CopyToClipboard(Host.GetCodeBlockText(Block.BlockId));
            e.Handled = true;
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            Focus();
            (int line, int column) = HitTestPosition(position, contentRect);
            State.SelectionStartLine = line;
            State.SelectionStartColumn = column;
            State.SelectionEndLine = line;
            State.SelectionEndColumn = column;
            _selecting = true;
            e.Pointer.Capture(this);
            Host.InvalidateBlock(Block.BlockId);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        Rect contentRect = ContentRect();
        Point position = e.GetPosition(this);

        bool hot = HasHeader && CopyButtonRect(contentRect).Contains(position);
        if (hot != _copyHot)
        {
            _copyHot = hot;
            Cursor = hot ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Ibeam);
            InvalidateVisual();
        }

        if (!_selecting)
        {
            return;
        }

        (int line, int column) = HitTestPosition(position, contentRect);
        State.SelectionEndLine = line;
        State.SelectionEndColumn = column;
        Host.InvalidateBlock(Block.BlockId);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Host.CopyToClipboard(GetSelectedText());
            e.Handled = true;
        }
        else if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            State.SelectionStartLine = 0;
            State.SelectionStartColumn = 0;
            State.SelectionEndLine = Math.Max(0, Block.TotalLineCount - 1);
            State.SelectionEndColumn = int.MaxValue / 2;
            Host.InvalidateBlock(Block.BlockId);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            State.ClearSelection();
            Host.InvalidateBlock(Block.BlockId);
        }
    }

    private string GetSelectedText()
    {
        string full = Host.GetCodeBlockText(Block.BlockId);
        if (!State.HasSelection)
        {
            return full;
        }

        (int startLine, int startColumn, int endLine, int endColumn) = NormalizedSelection(State);

        string[] lines = full.Split('\n');
        var builder = new System.Text.StringBuilder();

        for (int i = startLine; i <= Math.Min(endLine, lines.Length - 1); i++)
        {
            if (i < 0)
            {
                continue;
            }

            string line = lines[i];
            int from = i == startLine ? Math.Min(startColumn, line.Length) : 0;
            int to = i == endLine ? Math.Min(endColumn, line.Length) : line.Length;

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line.AsSpan(from, Math.Max(0, to - from)));
        }

        return builder.Length == 0 ? full : builder.ToString();
    }

    private static (int StartLine, int StartColumn, int EndLine, int EndColumn) NormalizedSelection(CodeBlockState state)
    {
        bool forward = state.SelectionStartLine < state.SelectionEndLine ||
                       (state.SelectionStartLine == state.SelectionEndLine && state.SelectionStartColumn <= state.SelectionEndColumn);

        return forward
            ? (state.SelectionStartLine, state.SelectionStartColumn, state.SelectionEndLine, state.SelectionEndColumn)
            : (state.SelectionEndLine, state.SelectionEndColumn, state.SelectionStartLine, state.SelectionStartColumn);
    }

    private Rect ContentRect() =>
        new(ContentLeft, TopSpacing, Math.Max(0, Bounds.Width - ContentLeft), Math.Max(0, Bounds.Height - TopSpacing));

    private (int Line, int Column) HitTestPosition(Point position, Rect contentRect)
    {
        double padding = Theme.CodePadding;
        bool first = Block.SegmentRole is CodeSegmentRole.First or CodeSegmentRole.Only;

        double textTop = contentRect.Y + _headerHeight + (first ? padding : 0);
        double textLeft = contentRect.X + padding + _gutterWidth - State.HorizontalOffset;

        int localLine = (int)Math.Floor((position.Y - textTop) / Math.Max(1, _lineHeight));
        localLine = Math.Clamp(localLine, 0, Math.Max(0, Block.LineCount - 1));

        int column = (int)Math.Round((position.X - textLeft) / Math.Max(1, _charWidth));
        column = Math.Max(0, column);

        return (Block.FirstLineNumber - 1 + localLine, column);
    }

    private int LineLength(int localLine)
    {
        string text = Block.CodeText ?? string.Empty;
        int index = 0;
        for (int i = 0; i < localLine; i++)
        {
            int next = text.IndexOf('\n', index);
            if (next < 0)
            {
                return 0;
            }

            index = next + 1;
        }

        int end = text.IndexOf('\n', index);
        return (end < 0 ? text.Length : end) - index;
    }
}
