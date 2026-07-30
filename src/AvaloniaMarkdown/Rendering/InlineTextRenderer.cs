using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using AvaloniaMarkdown.Ast;

namespace AvaloniaMarkdown.Rendering;

/// <summary>
/// Builds a single <see cref="TextLayout"/> for a whole block from its flat run table.
/// </summary>
/// <remarks>
/// <para>
/// This is the reason the renderer stays cheap: a paragraph containing bold, italic, code and
/// link fragments is <b>one</b> visual with one text layout, not a nest of <c>TextBlock</c>s
/// inside a <c>WrapPanel</c>. Styling is expressed with
/// <see cref="ValueSpan{T}"/> style overrides, which Avalonia's text shaper consumes directly.
/// </para>
/// </remarks>
public static class InlineTextRenderer
{
    private static readonly TextDecorationCollection UnderlineAndStrikethrough = CreateCombinedDecorations();

    /// <summary>Creates a wrapped text layout for <paramref name="content"/>.</summary>
    public static TextLayout CreateLayout(
        InlineContent content,
        MarkdownTheme theme,
        double fontSize,
        bool baseBold,
        IBrush baseForeground,
        double maxWidth,
        TextAlignment alignment = TextAlignment.Left,
        TextWrapping wrapping = TextWrapping.Wrap)
    {
        Typeface baseTypeface = theme.GetTypeface(baseBold, italic: false, monospace: false);

        IReadOnlyList<ValueSpan<TextRunProperties>>? overrides =
            BuildOverrides(content, theme, fontSize, baseBold, baseForeground);

        return new TextLayout(
            content.Text,
            baseTypeface,
            fontSize,
            baseForeground,
            alignment,
            wrapping,
            maxWidth: double.IsFinite(maxWidth) && maxWidth > 0 ? maxWidth : double.PositiveInfinity,
            lineHeight: theme.LineHeight,
            textStyleOverrides: overrides);
    }

    private static IReadOnlyList<ValueSpan<TextRunProperties>>? BuildOverrides(
        InlineContent content,
        MarkdownTheme theme,
        double fontSize,
        bool baseBold,
        IBrush baseForeground)
    {
        InlineRun[] runs = content.Runs;
        if (runs.Length == 0)
        {
            return null;
        }

        if (runs.Length == 1 && runs[0].Style == InlineStyle.None)
        {
            return null;
        }

        var result = new List<ValueSpan<TextRunProperties>>(runs.Length);

        foreach (InlineRun run in runs)
        {
            if (run.Length <= 0)
            {
                continue;
            }

            InlineStyle style = run.Style;
            bool bold = baseBold || (style & InlineStyle.Bold) != 0;
            bool italic = (style & InlineStyle.Italic) != 0;
            bool code = (style & InlineStyle.Code) != 0;

            IBrush foreground = baseForeground;
            IBrush? background = null;

            if (code)
            {
                foreground = theme.InlineCodeForeground;
                background = theme.InlineCodeBackground;
            }

            if ((style & InlineStyle.Link) != 0)
            {
                foreground = theme.LinkForeground;
            }

            if ((style & InlineStyle.Image) != 0)
            {
                foreground = theme.MutedForeground;
            }

            if ((style & InlineStyle.Highlight) != 0)
            {
                background = theme.HighlightBackground;
            }

            TextDecorationCollection? decorations = null;
            bool strike = (style & InlineStyle.Strikethrough) != 0;
            bool underline = (style & (InlineStyle.Underline | InlineStyle.Link)) != 0;

            if (strike && underline)
            {
                decorations = UnderlineAndStrikethrough;
            }
            else if (strike)
            {
                decorations = TextDecorations.Strikethrough;
            }
            else if (underline)
            {
                decorations = TextDecorations.Underline;
            }

            BaselineAlignment baseline = BaselineAlignment.Baseline;
            double size = code ? theme.CodeFontSize : fontSize;

            if ((style & InlineStyle.Superscript) != 0)
            {
                baseline = BaselineAlignment.Superscript;
                size *= 0.75;
            }
            else if ((style & InlineStyle.Subscript) != 0)
            {
                baseline = BaselineAlignment.Subscript;
                size *= 0.75;
            }

            var properties = new GenericTextRunProperties(
                theme.GetTypeface(bold, italic, code),
                size,
                decorations,
                foreground,
                background,
                baseline);

            result.Add(new ValueSpan<TextRunProperties>(run.Start, run.Length, properties));
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// Resolves the link target under <paramref name="point"/>, or <c>null</c>.
    /// </summary>
    public static InlineTarget? HitTestTarget(TextLayout layout, InlineContent content, Point point)
    {
        if (content.Targets.Length == 0)
        {
            return null;
        }

        TextHitTestResult hit = layout.HitTestPoint(point);
        if (!hit.IsInside)
        {
            return null;
        }

        int runIndex = content.FindRun(hit.TextPosition);
        if (runIndex < 0)
        {
            return null;
        }

        InlineRun run = content.Runs[runIndex];
        if (run.TargetId < 0 || (run.Style & (InlineStyle.Link | InlineStyle.Image)) == 0)
        {
            return null;
        }

        return content.Targets[run.TargetId];
    }

    private static TextDecorationCollection CreateCombinedDecorations()
    {
        var collection = new TextDecorationCollection();
        foreach (TextDecoration decoration in TextDecorations.Underline)
        {
            collection.Add(decoration);
        }

        foreach (TextDecoration decoration in TextDecorations.Strikethrough)
        {
            collection.Add(decoration);
        }

        return collection;
    }
}
