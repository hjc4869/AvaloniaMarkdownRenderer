using AvaloniaMarkdown.Text;

namespace AvaloniaMarkdown.Ast;

/// <summary>The structural kind of a markdown block.</summary>
public enum BlockKind
{
    Document,
    Paragraph,
    Heading,
    FencedCode,
    IndentedCode,
    ThematicBreak,
    BlockQuote,
    List,
    ListItem,
    Table,
    HtmlBlock,
}

/// <summary>
/// A mutable node in the incremental markdown AST.
/// </summary>
/// <remarks>
/// <para>
/// Nodes are built by <see cref="Parsing.BlockParser"/> as lines arrive. A node is
/// <see cref="IsOpen"/> while further lines may still extend it; once closed it is
/// treated as immutable for the remainder of the document's life, which is what makes
/// the frozen-prefix optimisation in the flattener sound.
/// </para>
/// <para>
/// Nodes never leave the parser thread. Content destined for the UI is materialised into
/// <see cref="Flattening.FlatBlock"/> instances first.
/// </para>
/// </remarks>
public sealed class MdNode
{
    private static int _nextId;

    internal MdNode(BlockKind kind)
    {
        Kind = kind;
        Id = Interlocked.Increment(ref _nextId);
        IsOpen = true;
    }

    /// <summary>Process-wide unique, stable identity used as the diff key.</summary>
    public int Id { get; }

    /// <summary>Incremented every time the node's rendered content changes.</summary>
    public int Version { get; internal set; }

    public BlockKind Kind { get; internal set; }

    public MdNode? Parent { get; internal set; }

    /// <summary>Child blocks for container kinds (<see cref="BlockKind.Document"/>, quote, list, list item).</summary>
    public List<MdNode> Children { get; } = new();

    /// <summary>Raw content lines for leaf kinds. Spans point into the owning <see cref="TextBuffer"/>.</summary>
    public List<SourceSpan> Lines { get; } = new();

    /// <summary>False once the block can no longer be extended by subsequent lines.</summary>
    public bool IsOpen { get; internal set; }

    // ---- Heading -------------------------------------------------------
    public int HeadingLevel { get; internal set; }

    // ---- Fenced code ---------------------------------------------------
    public char FenceChar { get; internal set; }
    public int FenceLength { get; internal set; }
    public int FenceIndent { get; internal set; }

    /// <summary>Info string of a fenced code block, e.g. <c>csharp</c>.</summary>
    public string? Info { get; internal set; }

    /// <summary>True while the closing fence has not been seen (streaming state).</summary>
    public bool IsUnterminated { get; internal set; }

    // ---- Lists ---------------------------------------------------------
    public bool IsOrdered { get; internal set; }
    public int ListStart { get; internal set; }
    public char ListMarker { get; internal set; }
    public bool IsTight { get; internal set; } = true;

    /// <summary>Column at which a list item's content begins (used for continuation matching).</summary>
    public int ContentIndent { get; internal set; }

    /// <summary>Non-null for GFM task list items.</summary>
    public bool? TaskChecked { get; internal set; }

    // ---- Tables --------------------------------------------------------
    public TableAlignment[]? ColumnAlignments { get; internal set; }

    // ---- Block quotes --------------------------------------------------
    /// <summary>Nesting depth of enclosing block quotes, cached at build time.</summary>
    public int QuoteDepth { get; internal set; }

    /// <summary>Nesting depth of enclosing lists, cached at build time.</summary>
    public int ListDepth { get; internal set; }

    /// <summary>Cached inline parse, keyed by <see cref="InlineCacheVersion"/>.</summary>
    internal InlineContent? CachedInlines { get; set; }

    internal int InlineCacheVersion { get; set; } = -1;

    /// <summary>Cached flattening output for this node, valid while <see cref="FlatCacheVersion"/> matches.</summary>
    internal object? FlatCache { get; set; }

    internal int FlatCacheVersion { get; set; } = -1;

    /// <summary>Shared horizontal-scroll/selection state for the segments of a code block.</summary>
    internal Flattening.CodeBlockState? CachedCodeState { get; set; }

    /// <summary>
    /// Number of leading children whose flattened output has already been promoted into the
    /// permanent (frozen) prefix of the render list.
    /// </summary>
    internal int FlatFrozenChildIndex { get; set; }

    /// <summary>Marker used by the speculative-line undo journal to snapshot a node at most once per line.</summary>
    internal int JournalMark { get; set; } = -1;

    /// <summary>Monotonic creation order within the parser, used to restore recycle-bin ordering.</summary>
    internal int ParseSequence { get; set; }

    public bool IsContainer => Kind is BlockKind.Document or BlockKind.BlockQuote or BlockKind.List or BlockKind.ListItem;

    public bool IsLeaf => !IsContainer;

    public MdNode? LastChild => Children.Count > 0 ? Children[^1] : null;

    internal void Touch() => Version++;

    /// <summary>
    /// Drops state that is only needed while a block can still change. Called once a block's
    /// rendered output has been promoted into the immutable frozen prefix, after which the node is
    /// never visited again by the flattener.
    /// </summary>
    internal void ReleaseRetainedState()
    {
        if (IsOpen)
        {
            return;
        }

        Lines.Clear();
        Lines.TrimExcess();
        CachedInlines = null;
        InlineCacheVersion = -1;
        FlatCache = null;
        FlatCacheVersion = -1;
        CachedCodeState = null;

        for (int i = 0; i < Children.Count; i++)
        {
            Children[i].ReleaseRetainedState();
        }
    }

    /// <summary>
    /// Resets the node so its identity (and therefore the Avalonia control bound to it) can be
    /// reused after a speculative parse of the trailing partial line was rolled back.
    /// </summary>
    internal void ResetForReuse(BlockKind kind)
    {
        Kind = kind;
        Parent = null;
        Children.Clear();
        Lines.Clear();
        IsOpen = true;
        HeadingLevel = 0;
        FenceChar = '\0';
        FenceLength = 0;
        FenceIndent = 0;
        Info = null;
        IsUnterminated = false;
        IsOrdered = false;
        ListStart = 0;
        ListMarker = '\0';
        IsTight = true;
        ContentIndent = 0;
        TaskChecked = null;
        ColumnAlignments = null;
        QuoteDepth = 0;
        ListDepth = 0;
        CachedInlines = null;
        InlineCacheVersion = -1;
        FlatCache = null;
        FlatCacheVersion = -1;
        CachedCodeState = null;
        FlatFrozenChildIndex = 0;
        JournalMark = -1;
        Version++;
    }

    public override string ToString() => $"{Kind}#{Id} v{Version} {(IsOpen ? "open" : "closed")} lines={Lines.Count} children={Children.Count}";
}
