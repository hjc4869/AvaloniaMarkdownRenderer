using Avalonia.Media;

namespace AvaloniaMarkdown.Rendering;

/// <summary>
/// Fonts, brushes and metrics used by every block view.
/// </summary>
/// <remarks>
/// A theme is an immutable-by-convention value object. Typefaces are resolved once and cached per
/// style combination so that building a text layout for a streamed token never hits font matching.
/// </remarks>
public sealed class MarkdownTheme
{
    private Typeface[]? _typefaces;
    private Typeface[]? _codeTypefaces;

    public MarkdownTheme()
    {
        FontFamily = FontFamily.Default;
        CodeFontFamily = new FontFamily("Cascadia Mono,Cascadia Code,JetBrains Mono,Consolas,Menlo,DejaVu Sans Mono,Liberation Mono,monospace");
    }

    /// <summary>Theme tuned for light backgrounds.</summary>
    public static MarkdownTheme Light { get; } = CreateLight();

    /// <summary>Theme tuned for dark backgrounds.</summary>
    public static MarkdownTheme Dark { get; } = CreateDark();

    // ---- Fonts ---------------------------------------------------------
    public FontFamily FontFamily { get; init; }

    public FontFamily CodeFontFamily { get; init; }

    public double FontSize { get; init; } = 14;

    public double CodeFontSize { get; init; } = 13;

    public double LineHeight { get; init; } = double.NaN;

    /// <summary>Font size multipliers for H1..H6.</summary>
    public double[] HeadingScales { get; init; } = { 2.0, 1.55, 1.3, 1.12, 1.0, 0.92 };

    // ---- Brushes -------------------------------------------------------

    /// <summary>Page background painted behind the document. Null keeps the parent background.</summary>
    public IBrush? Background { get; init; }

    public IBrush Foreground { get; init; } = Brushes.Black;

    public IBrush MutedForeground { get; init; } = new SolidColorBrush(Color.FromRgb(0x6a, 0x73, 0x7d));

    public IBrush LinkForeground { get; init; } = new SolidColorBrush(Color.FromRgb(0x03, 0x66, 0xd6));

    public IBrush InlineCodeForeground { get; init; } = new SolidColorBrush(Color.FromRgb(0xd7, 0x3a, 0x49));

    public IBrush InlineCodeBackground { get; init; } = new SolidColorBrush(Color.FromRgb(0xf3, 0xf4, 0xf6));

    public IBrush CodeBackground { get; init; } = new SolidColorBrush(Color.FromRgb(0xf6, 0xf8, 0xfa));

    public IBrush CodeForeground { get; init; } = new SolidColorBrush(Color.FromRgb(0x24, 0x29, 0x2e));

    public IBrush CodeBorder { get; init; } = new SolidColorBrush(Color.FromRgb(0xe1, 0xe4, 0xe8));

    public IBrush QuoteBar { get; init; } = new SolidColorBrush(Color.FromRgb(0xdf, 0xe2, 0xe5));

    public IBrush QuoteForeground { get; init; } = new SolidColorBrush(Color.FromRgb(0x6a, 0x73, 0x7d));

    public IBrush RuleBrush { get; init; } = new SolidColorBrush(Color.FromRgb(0xe1, 0xe4, 0xe8));

    public IBrush TableBorder { get; init; } = new SolidColorBrush(Color.FromRgb(0xd0, 0xd7, 0xde));

    public IBrush TableHeaderBackground { get; init; } = new SolidColorBrush(Color.FromRgb(0xf6, 0xf8, 0xfa));

    public IBrush SelectionBrush { get; init; } = new SolidColorBrush(Color.FromArgb(0x66, 0x33, 0x99, 0xff));

    public IBrush HighlightBackground { get; init; } = new SolidColorBrush(Color.FromRgb(0xff, 0xf3, 0xa3));

    public IBrush MarkerForeground { get; init; } = new SolidColorBrush(Color.FromRgb(0x57, 0x60, 0x6a));

    public IBrush StreamingCaretBrush { get; init; } = new SolidColorBrush(Color.FromArgb(0xaa, 0x03, 0x66, 0xd6));

    // ---- Metrics -------------------------------------------------------
    public double BlockSpacing { get; init; } = 10;

    public double TightBlockSpacing { get; init; } = 2;

    public double ListIndent { get; init; } = 24;

    public double QuoteIndent { get; init; } = 18;

    public double QuoteBarWidth { get; init; } = 3;

    public double CodePadding { get; init; } = 10;

    public double TableCellPadding { get; init; } = 6;

    public double ImageMaxHeight { get; init; } = 480;

    /// <summary>Show a line-number gutter inside fenced code blocks.</summary>
    public bool ShowCodeLineNumbers { get; init; }

    /// <summary>Show the language label in the code block header.</summary>
    public bool ShowCodeLanguageLabel { get; init; } = true;

    /// <summary>Resolved typeface for a text style combination.</summary>
    public Typeface GetTypeface(bool bold, bool italic, bool monospace)
    {
        Typeface[] set = monospace
            ? _codeTypefaces ??= BuildTypefaces(CodeFontFamily)
            : _typefaces ??= BuildTypefaces(FontFamily);

        return set[(bold ? 1 : 0) | (italic ? 2 : 0)];
    }

    public double GetHeadingSize(int level) => FontSize * HeadingScales[Math.Clamp(level, 1, 6) - 1];

    private static Typeface[] BuildTypefaces(FontFamily family)
    {
        var result = new Typeface[4];
        for (int i = 0; i < 4; i++)
        {
            FontWeight weight = (i & 1) != 0 ? FontWeight.Bold : FontWeight.Normal;
            FontStyle style = (i & 2) != 0 ? FontStyle.Italic : FontStyle.Normal;
            result[i] = new Typeface(family, style, weight);
        }

        return result;
    }

    private static MarkdownTheme CreateLight() => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0xff, 0xff, 0xff)),
    };

    private static MarkdownTheme CreateDark() => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x0d, 0x11, 0x17)),
        Foreground = new SolidColorBrush(Color.FromRgb(0xe6, 0xed, 0xf3)),
        MutedForeground = new SolidColorBrush(Color.FromRgb(0x8b, 0x94, 0x9e)),
        LinkForeground = new SolidColorBrush(Color.FromRgb(0x58, 0xa6, 0xff)),
        InlineCodeForeground = new SolidColorBrush(Color.FromRgb(0xff, 0x7b, 0x72)),
        InlineCodeBackground = new SolidColorBrush(Color.FromRgb(0x26, 0x2c, 0x36)),
        CodeBackground = new SolidColorBrush(Color.FromRgb(0x16, 0x1b, 0x22)),
        CodeForeground = new SolidColorBrush(Color.FromRgb(0xe6, 0xed, 0xf3)),
        CodeBorder = new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x3d)),
        QuoteBar = new SolidColorBrush(Color.FromRgb(0x3d, 0x44, 0x4d)),
        QuoteForeground = new SolidColorBrush(Color.FromRgb(0x8b, 0x94, 0x9e)),
        RuleBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x3d)),
        TableBorder = new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x3d)),
        TableHeaderBackground = new SolidColorBrush(Color.FromRgb(0x21, 0x26, 0x2d)),
        HighlightBackground = new SolidColorBrush(Color.FromRgb(0x6b, 0x53, 0x00)),
        MarkerForeground = new SolidColorBrush(Color.FromRgb(0x8b, 0x94, 0x9e)),
    };
}
