using AvaloniaMarkdown.Ast;

namespace AvaloniaMarkdown.Flattening;

/// <summary>Render-level classification of a flat block.</summary>
public enum FlatBlockKind
{
    Paragraph,
    Heading,
    Code,
    ThematicBreak,
    Table,
    Html,
    Image,
}

/// <summary>Position of a code segment within its logical code block.</summary>
public enum CodeSegmentRole
{
    Only,
    First,
    Middle,
    Last,
}

/// <summary>A materialised table, ready for rendering.</summary>
public sealed class TableModel
{
    public TableModel(InlineContent[] header, InlineContent[][] rows, TableAlignment[] alignments)
    {
        Header = header;
        Rows = rows;
        Alignments = alignments;
    }

    public InlineContent[] Header { get; }

    public InlineContent[][] Rows { get; }

    public TableAlignment[] Alignments { get; }

    public int ColumnCount => Alignments.Length;
}

/// <summary>
/// Mutable state shared by all segments of one code block (horizontal scroll offset and
/// selection), so a block split across virtualised segments still behaves as one unit.
/// </summary>
public sealed class CodeBlockState
{
    public double HorizontalOffset { get; set; }

    public double MaxLineWidth { get; set; }

    public int SelectionStartLine { get; set; } = -1;

    public int SelectionStartColumn { get; set; }

    public int SelectionEndLine { get; set; } = -1;

    public int SelectionEndColumn { get; set; }

    public bool HasSelection => SelectionStartLine >= 0 && SelectionEndLine >= 0;

    public void ClearSelection()
    {
        SelectionStartLine = -1;
        SelectionEndLine = -1;
    }
}

/// <summary>
/// One immutable, self-contained render item.
/// </summary>
/// <remarks>
/// <para>
/// Flat blocks are the currency between the parser thread and the UI thread. They contain only
/// materialised values (strings, run tables, bitmaps URLs) — never a reference back into the
/// mutable AST — which is what makes the pipeline thread-safe without locking.
/// </para>
/// <para>
/// The diff key is <see cref="BlockId"/> + <see cref="SegmentIndex"/>; <see cref="Version"/>
/// decides whether a realised control can be kept and updated in place.
/// </para>
/// </remarks>
public sealed class FlatBlock
{
    public required int BlockId { get; init; }

    public int SegmentIndex { get; init; }

    public required int Version { get; init; }

    public required FlatBlockKind Kind { get; init; }

    /// <summary>Number of enclosing block quotes; drives the quote bars drawn by the view.</summary>
    public int QuoteDepth { get; init; }

    /// <summary>Number of enclosing list levels; drives horizontal indentation.</summary>
    public int IndentLevel { get; init; }

    /// <summary>True while the block may still receive more streamed content.</summary>
    public bool IsOpen { get; init; }

    /// <summary>List bullet or number rendered to the left of the block, when it starts a list item.</summary>
    public string? Marker { get; init; }

    /// <summary>Non-null for GFM task list items.</summary>
    public bool? TaskChecked { get; init; }

    /// <summary>True when the enclosing list is tight (no paragraph spacing between items).</summary>
    public bool IsTightList { get; init; } = true;

    public InlineContent Inlines { get; init; } = InlineContent.Empty;

    public int HeadingLevel { get; init; }

    // ---- Code ----------------------------------------------------------
    public string? CodeText { get; init; }

    public string? Language { get; init; }

    public int FirstLineNumber { get; init; }

    public int LineCount { get; init; }

    public int TotalLineCount { get; init; }

    public CodeSegmentRole SegmentRole { get; init; }

    public CodeBlockState? CodeState { get; init; }

    // ---- Table ---------------------------------------------------------
    public TableModel? Table { get; init; }

    // ---- Image ---------------------------------------------------------
    public string? ImageUrl { get; init; }

    public string? ImageAlt { get; init; }

    public string? ImageTitle { get; init; }

    /// <summary>Composite diff key.</summary>
    public long Key => ((long)BlockId << 20) | (uint)SegmentIndex;

    public override string ToString() => $"{Kind}#{BlockId}.{SegmentIndex} v{Version}";
}
