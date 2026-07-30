namespace AvaloniaMarkdown;

/// <summary>
/// Parsing and layout-independent behaviour switches for the markdown engine.
/// </summary>
public sealed class MarkdownOptions
{
    /// <summary>Shared default instance. Treat as read-only.</summary>
    public static MarkdownOptions Default { get; } = new();

    /// <summary>
    /// When true (GitHub comment behaviour, and what chat UIs expect) a single newline inside a
    /// paragraph produces a line break instead of a space.
    /// </summary>
    public bool SoftLineBreaksAsHardBreaks { get; init; } = true;

    /// <summary>Detect bare <c>http(s)://</c> and <c>www.</c> URLs and render them as links.</summary>
    public bool EnableAutoLinks { get; init; } = true;

    /// <summary>Enable GFM <c>~~strikethrough~~</c>.</summary>
    public bool EnableStrikethrough { get; init; } = true;

    /// <summary>Enable GFM task list checkboxes.</summary>
    public bool EnableTaskLists { get; init; } = true;

    /// <summary>Enable GFM pipe tables.</summary>
    public bool EnableTables { get; init; } = true;

    /// <summary>
    /// While a block is still being streamed, close dangling emphasis delimiters so that
    /// <c>**bol</c> already renders bold instead of showing raw asterisks that vanish later.
    /// </summary>
    public bool AutoCloseStreamingEmphasis { get; init; } = true;

    /// <summary>
    /// Number of source lines packed into a single virtualised code-block segment. Large code
    /// blocks are split so that only the visible portion is ever laid out.
    /// </summary>
    public int CodeBlockChunkLines { get; init; } = 128;

    /// <summary>Maximum number of characters rendered for a single inline construct.</summary>
    public int MaxInlineNestingDepth { get; init; } = 32;
}
