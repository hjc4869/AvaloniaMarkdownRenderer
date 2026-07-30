namespace AvaloniaMarkdown.Ast;

/// <summary>Styling applied to a contiguous run of inline text.</summary>
[Flags]
public enum InlineStyle
{
    None = 0,
    Bold = 1 << 0,
    Italic = 1 << 1,
    Strikethrough = 1 << 2,
    Code = 1 << 3,
    Link = 1 << 4,
    Image = 1 << 5,
    Underline = 1 << 6,
    Highlight = 1 << 7,
    Superscript = 1 << 8,
    Subscript = 1 << 9,

    /// <summary>Set on runs that were produced by an unterminated construct while streaming.</summary>
    Provisional = 1 << 10,
}

/// <summary>Horizontal alignment of a GFM table column.</summary>
public enum TableAlignment
{
    None = 0,
    Left,
    Center,
    Right,
}

/// <summary>
/// A contiguous slice of <see cref="InlineContent.Text"/> that shares identical styling.
/// </summary>
/// <remarks>
/// Runs are the unit of text rendering: a whole paragraph is drawn as a single
/// <c>TextLayout</c> whose style overrides are derived from this array. No control is
/// created per formatting fragment.
/// </remarks>
public readonly struct InlineRun
{
    public InlineRun(int start, int length, InlineStyle style, int targetId = -1)
    {
        Start = start;
        Length = length;
        Style = style;
        TargetId = targetId;
    }

    public int Start { get; }

    public int Length { get; }

    public InlineStyle Style { get; }

    /// <summary>Index into <see cref="InlineContent.Targets"/>, or -1 when the run has no target.</summary>
    public int TargetId { get; }

    public int End => Start + Length;

    public override string ToString() => $"[{Start}..{End}) {Style}";
}

/// <summary>A link or image destination referenced by one or more <see cref="InlineRun"/>s.</summary>
public sealed class InlineTarget
{
    public InlineTarget(string url, string? title, bool isImage)
    {
        Url = url;
        Title = title;
        IsImage = isImage;
    }

    public string Url { get; }

    public string? Title { get; }

    public bool IsImage { get; }
}

/// <summary>
/// Fully materialised inline content for a single block: plain text plus a flat run table.
/// Immutable and safe to hand to the UI thread.
/// </summary>
public sealed class InlineContent
{
    public static readonly InlineContent Empty = new(string.Empty, Array.Empty<InlineRun>(), Array.Empty<InlineTarget>());

    public InlineContent(string text, InlineRun[] runs, InlineTarget[] targets)
    {
        Text = text;
        Runs = runs;
        Targets = targets;
    }

    public string Text { get; }

    public InlineRun[] Runs { get; }

    public InlineTarget[] Targets { get; }

    public bool IsEmpty => Text.Length == 0;

    public bool HasTargets => Targets.Length > 0;

    /// <summary>Returns the run covering <paramref name="characterIndex"/>, or -1.</summary>
    public int FindRun(int characterIndex)
    {
        InlineRun[] runs = Runs;
        int lo = 0;
        int hi = runs.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            InlineRun run = runs[mid];
            if (characterIndex < run.Start)
            {
                hi = mid - 1;
            }
            else if (characterIndex >= run.End)
            {
                lo = mid + 1;
            }
            else
            {
                return mid;
            }
        }

        return -1;
    }
}
