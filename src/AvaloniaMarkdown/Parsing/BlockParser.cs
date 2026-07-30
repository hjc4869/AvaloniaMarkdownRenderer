using AvaloniaMarkdown.Ast;
using AvaloniaMarkdown.Text;

namespace AvaloniaMarkdown.Parsing;

/// <summary>
/// Line-driven incremental block parser.
/// </summary>
/// <remarks>
/// <para>
/// The parser consumes one source line at a time and mutates only the <i>open</i> path of the
/// AST (root → … → tip). Closed blocks are never revisited, which gives O(new text) parsing
/// regardless of document size.
/// </para>
/// <para>
/// Streaming introduces a trailing partial line that has not been terminated by a newline yet.
/// That line is parsed <i>speculatively</i>: every mutation is journalled so the parse can be
/// rolled back verbatim when more characters arrive. Nodes removed by a rollback are pushed into
/// a recycle bin and handed back out (with their identity intact) by the next speculative parse,
/// which is what keeps Avalonia controls stable — and therefore flicker free — while a block is
/// still being streamed.
/// </para>
/// </remarks>
public sealed class BlockParser
{
    private readonly TextBuffer _buffer;
    private readonly List<NodeSnapshot> _journal = new();
    private readonly List<MdNode> _recycleBin = new();
    private readonly List<MdNode> _rollbackScratch = new();

    private MdNode _root;
    private bool _journalActive;
    private int _lineToken;
    private int _parseSequence;
    private int _recycleCursor;
    private bool _lastLineBlank;
    private bool _savedLastLineBlank;

    public BlockParser(TextBuffer buffer)
    {
        _buffer = buffer;
        _root = new MdNode(BlockKind.Document);
    }

    /// <summary>Root of the document tree. Always open.</summary>
    public MdNode Root => _root;

    /// <summary>True when a speculative (unterminated) line is currently applied to the tree.</summary>
    public bool HasSpeculativeLine { get; private set; }

    /// <summary>Discards all state and starts a fresh document.</summary>
    public void Reset()
    {
        _journal.Clear();
        _recycleBin.Clear();
        _recycleCursor = 0;
        _lastLineBlank = false;
        HasSpeculativeLine = false;
        _root = new MdNode(BlockKind.Document);
    }

    /// <summary>Begins an append cycle by rolling back any speculative line from the previous cycle.</summary>
    public void BeginAppendCycle()
    {
        RollbackSpeculative();
        _recycleCursor = 0;
    }

    /// <summary>Ends an append cycle, releasing recycled nodes that were not reused.</summary>
    public void EndAppendCycle()
    {
        _recycleBin.Clear();
        _recycleCursor = 0;
    }

    /// <summary>Feeds a line that is terminated by a newline and therefore final.</summary>
    public void ProcessCommittedLine(SourceSpan line)
    {
        _lineToken++;
        _journalActive = false;
        ProcessLineCore(line);
    }

    /// <summary>
    /// Feeds the trailing partial line. The resulting mutations can be undone by
    /// <see cref="RollbackSpeculative"/>.
    /// </summary>
    public void ProcessSpeculativeLine(SourceSpan line)
    {
        _journal.Clear();
        _savedLastLineBlank = _lastLineBlank;
        _lineToken++;
        _journalActive = true;
        HasSpeculativeLine = true;
        try
        {
            ProcessLineCore(line);
        }
        finally
        {
            _journalActive = false;
        }
    }

    /// <summary>Undoes the speculative line, restoring the tree to its last committed state.</summary>
    public void RollbackSpeculative()
    {
        if (!HasSpeculativeLine)
        {
            return;
        }

        _recycleBin.Clear();
        _rollbackScratch.Clear();

        for (int i = _journal.Count - 1; i >= 0; i--)
        {
            _journal[i].Restore(_rollbackScratch);
        }

        _journal.Clear();
        _lastLineBlank = _savedLastLineBlank;
        HasSpeculativeLine = false;

        if (_rollbackScratch.Count > 0)
        {
            _rollbackScratch.Sort(static (a, b) => a.ParseSequence.CompareTo(b.ParseSequence));
            _recycleBin.AddRange(_rollbackScratch);
            _rollbackScratch.Clear();
        }

        _recycleCursor = 0;
    }

    /// <summary>Closes every open block; called when the stream ends.</summary>
    public void CloseAll()
    {
        CloseRecursive(_root, closeSelf: false);
    }

    // ------------------------------------------------------------------
    // Core line algorithm
    // ------------------------------------------------------------------

    private void ProcessLineCore(SourceSpan lineSpan)
    {
        var cursor = new LineCursor(_buffer.Slice(lineSpan.Start, lineSpan.Length), lineSpan.Start);

        // --- Phase 1: match already-open containers -----------------------
        MdNode container = _root;
        bool allMatched = true;
        while (true)
        {
            MdNode? child = LastOpenChild(container);
            if (child is null || !child.IsContainer)
            {
                break;
            }

            if (TryMatchContainer(child, ref cursor))
            {
                container = child;
                continue;
            }

            allMatched = false;
            break;
        }

        MdNode? matchedLeaf = allMatched && LastOpenChild(container) is { IsContainer: false } leaf ? leaf : null;

        // --- Phase 2: continue an open leaf -------------------------------
        if (matchedLeaf is not null)
        {
            switch (matchedLeaf.Kind)
            {
                case BlockKind.FencedCode:
                    ContinueFencedCode(matchedLeaf, ref cursor);
                    return;

                case BlockKind.IndentedCode:
                    if (cursor.IsBlank)
                    {
                        AddLine(matchedLeaf, new SourceSpan(cursor.RemainingSpan.Start, 0));
                        _lastLineBlank = true;
                        return;
                    }

                    if (cursor.PeekIndent() >= 4)
                    {
                        cursor.SkipIndent(4);
                        AddLine(matchedLeaf, cursor.RemainingSpan);
                        _lastLineBlank = false;
                        return;
                    }

                    CloseNode(matchedLeaf);
                    matchedLeaf = null;
                    break;

                case BlockKind.HtmlBlock:
                    if (cursor.IsBlank)
                    {
                        CloseNode(matchedLeaf);
                        _lastLineBlank = true;
                        return;
                    }

                    AddLine(matchedLeaf, cursor.RemainingSpan);
                    _lastLineBlank = false;
                    return;

                case BlockKind.Table:
                    if (!cursor.IsBlank && !CanInterruptParagraph(ref cursor, container.Kind == BlockKind.List))
                    {
                        AddLine(matchedLeaf, cursor.RemainingSpan);
                        _lastLineBlank = false;
                        return;
                    }

                    CloseNode(matchedLeaf);
                    matchedLeaf = null;
                    break;
            }
        }

        // --- Phase 3: lazy paragraph continuation -------------------------
        if (!allMatched)
        {
            MdNode? openLeaf = DeepestOpenLeaf();
            if (openLeaf is { Kind: BlockKind.Paragraph } &&
                !cursor.IsBlank &&
                !CanInterruptParagraph(ref cursor, container.Kind == BlockKind.List))
            {
                cursor.SkipIndent(int.MaxValue);
                AddLine(openLeaf, cursor.RemainingSpan);
                _lastLineBlank = false;
                return;
            }

            CloseOpenChild(container);
        }

        // --- Phase 4: start new blocks ------------------------------------
        while (true)
        {
            bool inParagraph = LastOpenChild(container) is { Kind: BlockKind.Paragraph };
            int indent = cursor.PeekIndent();

            if (indent >= 4)
            {
                if (inParagraph)
                {
                    break;
                }

                cursor.SkipIndent(4);
                NormalizeContainerForLeaf(ref container);
                MdNode code = OpenChild(container, BlockKind.IndentedCode);
                AddLine(code, cursor.RemainingSpan);
                _lastLineBlank = false;
                return;
            }

            cursor.SkipIndent(indent);
            if (cursor.AtEnd)
            {
                break;
            }

            char c = cursor.Current;

            if (c == '>')
            {
                cursor.Advance();
                if (!cursor.AtEnd && cursor.Current == ' ')
                {
                    cursor.Advance();
                }
                else if (!cursor.AtEnd && cursor.Current == '\t')
                {
                    cursor.Advance();
                }

                CloseOpenChild(container);
                NormalizeContainerForLeaf(ref container);
                container = OpenChild(container, BlockKind.BlockQuote);
                continue;
            }

            if (c == '#' && TryStartAtxHeading(container, ref cursor))
            {
                _lastLineBlank = false;
                return;
            }

            if ((c == '`' || c == '~') && TryStartFencedCode(ref container, ref cursor))
            {
                _lastLineBlank = false;
                return;
            }

            if (inParagraph && TryParseSetextUnderline(cursor.Remaining, out int setextLevel))
            {
                ConvertParagraphToHeading(LastOpenChild(container)!, setextLevel);
                _lastLineBlank = false;
                return;
            }

            if (IsThematicBreak(cursor.Remaining))
            {
                CloseOpenChild(container);
                NormalizeContainerForLeaf(ref container);
                MdNode rule = OpenChild(container, BlockKind.ThematicBreak);
                CloseNode(rule);
                _lastLineBlank = false;
                return;
            }

            if (TryParseListMarker(ref cursor, inParagraph, out ListMarkerInfo marker))
            {
                CloseOpenChild(container);
                container = OpenListItem(container, in marker);
                continue;
            }

            if (!inParagraph && c == '<' && HtmlBlockScanner.IsBlockStart(cursor.Remaining))
            {
                NormalizeContainerForLeaf(ref container);
                MdNode html = OpenChild(container, BlockKind.HtmlBlock);
                AddLine(html, cursor.RemainingSpan);
                _lastLineBlank = false;
                return;
            }

            break;
        }

        // --- Phase 5: plain text ------------------------------------------
        if (cursor.IsBlank)
        {
            if (LastOpenChild(container) is { IsContainer: false } openLeafBlock &&
                openLeafBlock.Kind is BlockKind.Paragraph or BlockKind.Table)
            {
                CloseNode(openLeafBlock);
            }

            if (!_lastLineBlank)
            {
                MarkEnclosingListLoose(container);
            }

            _lastLineBlank = true;
            return;
        }

        MdNode? tip = LastOpenChild(container);
        if (tip is { Kind: BlockKind.Paragraph })
        {
            if (tip.Lines.Count >= 1 &&
                TableParser.TryParseDelimiterRow(cursor.Remaining, out TableAlignment[] alignments) &&
                TableParser.CountColumns(LineText(tip.Lines[^1])) == alignments.Length)
            {
                ConvertParagraphToTable(tip, alignments);
                _lastLineBlank = false;
                return;
            }

            AddLine(tip, cursor.RemainingSpan);
            _lastLineBlank = false;
            return;
        }

        NormalizeContainerForLeaf(ref container);
        if (LastOpenChild(container) is { IsContainer: false } stale)
        {
            CloseNode(stale);
        }

        MdNode paragraph = OpenChild(container, BlockKind.Paragraph);
        if (container.Kind == BlockKind.ListItem && container.Children.Count == 1)
        {
            TryReadTaskMarker(container, ref cursor);
        }

        AddLine(paragraph, cursor.RemainingSpan);
        _lastLineBlank = false;
    }

    // ------------------------------------------------------------------
    // Container matching
    // ------------------------------------------------------------------

    private bool TryMatchContainer(MdNode node, ref LineCursor cursor)
    {
        switch (node.Kind)
        {
            case BlockKind.List:
                return true;

            case BlockKind.BlockQuote:
            {
                int indent = cursor.PeekIndent();
                if (indent >= 4)
                {
                    return false;
                }

                LineCursor.CursorState state = cursor.Save();
                cursor.SkipIndent(indent);
                if (!cursor.AtEnd && cursor.Current == '>')
                {
                    cursor.Advance();
                    if (!cursor.AtEnd && (cursor.Current == ' ' || cursor.Current == '\t'))
                    {
                        cursor.Advance();
                    }

                    return true;
                }

                cursor.Restore(state);
                return false;
            }

            case BlockKind.ListItem:
            {
                if (cursor.IsBlank)
                {
                    return node.Children.Count > 0;
                }

                if (cursor.Column + cursor.PeekIndent() >= node.ContentIndent)
                {
                    cursor.SkipIndent(node.ContentIndent - cursor.Column);
                    return true;
                }

                return false;
            }

            default:
                return false;
        }
    }

    // ------------------------------------------------------------------
    // Block starts
    // ------------------------------------------------------------------

    private bool TryStartAtxHeading(MdNode container, ref LineCursor cursor)
    {
        LineCursor.CursorState state = cursor.Save();

        int level = 0;
        while (!cursor.AtEnd && cursor.Current == '#' && level < 7)
        {
            level++;
            cursor.Advance();
        }

        if (level is < 1 or > 6 || (!cursor.AtEnd && cursor.Current != ' ' && cursor.Current != '\t'))
        {
            cursor.Restore(state);
            return false;
        }

        cursor.SkipWhitespace();

        ReadOnlySpan<char> rest = cursor.Remaining;
        int end = rest.Length;
        while (end > 0 && (rest[end - 1] == ' ' || rest[end - 1] == '\t'))
        {
            end--;
        }

        int hashEnd = end;
        while (hashEnd > 0 && rest[hashEnd - 1] == '#')
        {
            hashEnd--;
        }

        if (hashEnd < end && (hashEnd == 0 || rest[hashEnd - 1] == ' ' || rest[hashEnd - 1] == '\t'))
        {
            end = hashEnd;
            while (end > 0 && (rest[end - 1] == ' ' || rest[end - 1] == '\t'))
            {
                end--;
            }
        }

        MdNode target = container;
        CloseOpenChild(target);
        NormalizeContainerForLeaf(ref target);
        MdNode heading = OpenChild(target, BlockKind.Heading);
        heading.HeadingLevel = level;
        AddLine(heading, cursor.SpanOf(cursor.Index, end));
        CloseNode(heading);
        return true;
    }

    private bool TryStartFencedCode(ref MdNode container, ref LineCursor cursor)
    {
        char fence = cursor.Current;
        int startIndex = cursor.Index;
        int indentColumn = cursor.Column;
        int count = 0;
        while (!cursor.AtEnd && cursor.Current == fence)
        {
            count++;
            cursor.Advance();
        }

        if (count < 3)
        {
            cursor.Restore(new LineCursor.CursorState(startIndex, indentColumn));
            return false;
        }

        ReadOnlySpan<char> info = cursor.Remaining.Trim();
        if (fence == '`' && info.IndexOf('`') >= 0)
        {
            cursor.Restore(new LineCursor.CursorState(startIndex, indentColumn));
            return false;
        }

        CloseOpenChild(container);
        NormalizeContainerForLeaf(ref container);
        MdNode code = OpenChild(container, BlockKind.FencedCode);
        code.FenceChar = fence;
        code.FenceLength = count;
        code.FenceIndent = indentColumn;
        code.IsUnterminated = true;
        code.Info = info.IsEmpty ? null : info.ToString();
        return true;
    }

    private void ContinueFencedCode(MdNode node, ref LineCursor cursor)
    {
        LineCursor.CursorState state = cursor.Save();
        int indent = cursor.PeekIndent();
        if (indent < 4)
        {
            cursor.SkipIndent(indent);
            int count = 0;
            while (!cursor.AtEnd && cursor.Current == node.FenceChar)
            {
                count++;
                cursor.Advance();
            }

            if (count >= node.FenceLength && cursor.IsBlank)
            {
                MutateNode(node);
                node.IsUnterminated = false;
                CloseNode(node);
                _lastLineBlank = false;
                return;
            }
        }

        cursor.Restore(state);
        cursor.SkipIndent(node.FenceIndent);
        AddLine(node, cursor.RemainingSpan);
        _lastLineBlank = false;
    }

    private MdNode OpenListItem(MdNode container, in ListMarkerInfo marker)
    {
        MdNode listContainer = container;

        if (listContainer.Kind == BlockKind.List && !MatchesListType(listContainer, in marker))
        {
            CloseNode(listContainer);
            listContainer = listContainer.Parent!;
        }

        if (listContainer.Kind != BlockKind.List)
        {
            NormalizeContainerForLeaf(ref listContainer);
            MdNode list = OpenChild(listContainer, BlockKind.List);
            list.IsOrdered = marker.IsOrdered;
            list.ListStart = marker.Start;
            list.ListMarker = marker.Marker;
            list.IsTight = true;
            listContainer = list;
        }

        if (_lastLineBlank && listContainer.Children.Count > 0)
        {
            MutateNode(listContainer);
            listContainer.IsTight = false;
            listContainer.Touch();
        }

        MdNode item = OpenChild(listContainer, BlockKind.ListItem);
        item.ContentIndent = marker.ContentIndent;
        return item;
    }

    private static bool MatchesListType(MdNode list, in ListMarkerInfo marker) =>
        list.IsOrdered == marker.IsOrdered && list.ListMarker == marker.Marker;

    private void TryReadTaskMarker(MdNode item, ref LineCursor cursor)
    {
        ReadOnlySpan<char> rest = cursor.Remaining;
        if (rest.Length < 3 || rest[0] != '[' || rest[2] != ']')
        {
            return;
        }

        char state = rest[1];
        bool? checkedState = state switch
        {
            ' ' => false,
            'x' or 'X' => true,
            _ => null,
        };

        if (checkedState is null)
        {
            return;
        }

        if (rest.Length > 3 && rest[3] != ' ' && rest[3] != '\t')
        {
            return;
        }

        MutateNode(item);
        item.TaskChecked = checkedState;
        item.Touch();
        cursor.Advance(3);
        if (!cursor.AtEnd && (cursor.Current == ' ' || cursor.Current == '\t'))
        {
            cursor.Advance();
        }
    }

    private void ConvertParagraphToHeading(MdNode paragraph, int level)
    {
        MutateNode(paragraph);
        paragraph.Kind = BlockKind.Heading;
        paragraph.HeadingLevel = level;
        paragraph.Touch();
        CloseNode(paragraph);
    }

    private void ConvertParagraphToTable(MdNode paragraph, TableAlignment[] alignments)
    {
        MutateNode(paragraph);

        // Everything before the header row stays a paragraph; in practice the header is the
        // last accumulated line, so trailing lines beyond it are moved into the table.
        if (paragraph.Lines.Count > 1)
        {
            SourceSpan header = paragraph.Lines[^1];
            paragraph.Lines.RemoveAt(paragraph.Lines.Count - 1);
            paragraph.Touch();
            CloseNode(paragraph);

            MdNode table = OpenChild(paragraph.Parent!, BlockKind.Table);
            table.ColumnAlignments = alignments;
            AddLine(table, header);
            return;
        }

        paragraph.Kind = BlockKind.Table;
        paragraph.ColumnAlignments = alignments;
        paragraph.Touch();
    }

    // ------------------------------------------------------------------
    // Recognition helpers
    // ------------------------------------------------------------------

    internal static bool IsThematicBreak(ReadOnlySpan<char> text)
    {
        char marker = '\0';
        int count = 0;

        foreach (char c in text)
        {
            if (c is ' ' or '\t')
            {
                continue;
            }

            if (c is '-' or '_' or '*')
            {
                if (marker == '\0')
                {
                    marker = c;
                }
                else if (marker != c)
                {
                    return false;
                }

                count++;
                continue;
            }

            return false;
        }

        return count >= 3;
    }

    internal static bool TryParseSetextUnderline(ReadOnlySpan<char> text, out int level)
    {
        level = 0;
        char marker = '\0';
        int count = 0;
        int i = 0;

        while (i < text.Length && (text[i] == '=' || text[i] == '-'))
        {
            if (marker == '\0')
            {
                marker = text[i];
            }
            else if (marker != text[i])
            {
                return false;
            }

            count++;
            i++;
        }

        if (count == 0)
        {
            return false;
        }

        while (i < text.Length)
        {
            if (text[i] != ' ' && text[i] != '\t')
            {
                return false;
            }

            i++;
        }

        level = marker == '=' ? 1 : 2;
        return true;
    }

    internal static bool TryParseListMarker(ref LineCursor cursor, bool inParagraph, out ListMarkerInfo info)
    {
        info = default;
        LineCursor.CursorState state = cursor.Save();

        char c = cursor.Current;
        bool ordered = false;
        int start = 1;
        char markerChar;

        if (c is '-' or '+' or '*')
        {
            markerChar = c;
            cursor.Advance();
        }
        else if (char.IsAsciiDigit(c))
        {
            int digits = 0;
            int value = 0;
            while (!cursor.AtEnd && char.IsAsciiDigit(cursor.Current) && digits < 9)
            {
                value = (value * 10) + (cursor.Current - '0');
                digits++;
                cursor.Advance();
            }

            if (cursor.AtEnd || (cursor.Current != '.' && cursor.Current != ')'))
            {
                cursor.Restore(state);
                return false;
            }

            markerChar = cursor.Current;
            ordered = true;
            start = value;
            cursor.Advance();
        }
        else
        {
            return false;
        }

        if (!cursor.AtEnd && cursor.Current != ' ' && cursor.Current != '\t')
        {
            cursor.Restore(state);
            return false;
        }

        bool blankContent = cursor.IsBlank;

        // A list item can only interrupt a paragraph when it has content and, if ordered, starts at 1.
        if (inParagraph && (blankContent || (ordered && start != 1)))
        {
            cursor.Restore(state);
            return false;
        }

        int markerEndColumn = cursor.Column;
        int spaces = cursor.PeekIndent();

        int contentIndent;
        if (blankContent || spaces > 4)
        {
            contentIndent = markerEndColumn + 1;
            if (!cursor.AtEnd && (cursor.Current == ' ' || cursor.Current == '\t'))
            {
                cursor.Advance();
            }
        }
        else
        {
            cursor.SkipIndent(spaces);
            contentIndent = cursor.Column;
        }

        info = new ListMarkerInfo(ordered, start, markerChar, contentIndent);
        return true;
    }

    /// <summary>Probe used for lazy-continuation and table termination decisions.</summary>
    /// <param name="cursor">Cursor positioned at the start of the line content.</param>
    /// <param name="inExistingList">
    /// True when the enclosing container is already a list, in which case a marker continues that
    /// list rather than interrupting a paragraph, and the "ordered lists must start at 1" rule
    /// does not apply.
    /// </param>
    private static bool CanInterruptParagraph(ref LineCursor cursor, bool inExistingList)
    {
        LineCursor.CursorState state = cursor.Save();
        try
        {
            int indent = cursor.PeekIndent();
            if (indent >= 4)
            {
                return false;
            }

            cursor.SkipIndent(indent);
            if (cursor.AtEnd)
            {
                return true;
            }

            char c = cursor.Current;
            if (c == '>')
            {
                return true;
            }

            if (c == '#')
            {
                int level = 0;
                while (!cursor.AtEnd && cursor.Current == '#')
                {
                    level++;
                    cursor.Advance();
                }

                return level is >= 1 and <= 6 && (cursor.AtEnd || cursor.Current == ' ' || cursor.Current == '\t');
            }

            if (c is '`' or '~')
            {
                int count = 0;
                while (!cursor.AtEnd && cursor.Current == c)
                {
                    count++;
                    cursor.Advance();
                }

                return count >= 3;
            }

            if (IsThematicBreak(cursor.Remaining))
            {
                return true;
            }

            return TryParseListMarker(ref cursor, inParagraph: !inExistingList, out _);
        }
        finally
        {
            cursor.Restore(state);
        }
    }

    // ------------------------------------------------------------------
    // Tree helpers
    // ------------------------------------------------------------------

    private static MdNode? LastOpenChild(MdNode container)
    {
        if (container.Children.Count == 0)
        {
            return null;
        }

        MdNode last = container.Children[^1];
        return last.IsOpen ? last : null;
    }

    private MdNode? DeepestOpenLeaf()
    {
        MdNode node = _root;
        while (true)
        {
            MdNode? child = LastOpenChild(node);
            if (child is null)
            {
                return node.IsLeaf && node != _root ? node : null;
            }

            if (child.IsLeaf)
            {
                return child;
            }

            node = child;
        }
    }

    private void NormalizeContainerForLeaf(ref MdNode container)
    {
        while (container.Kind == BlockKind.List)
        {
            MdNode parent = container.Parent!;
            CloseNode(container);
            container = parent;
        }
    }

    private MdNode OpenChild(MdNode parent, BlockKind kind)
    {
        CloseOpenChild(parent);

        MdNode node = RentNode(kind);
        node.Parent = parent;
        node.QuoteDepth = parent.QuoteDepth + (parent.Kind == BlockKind.BlockQuote ? 1 : 0);
        node.ListDepth = parent.ListDepth + (parent.Kind == BlockKind.ListItem ? 1 : 0);

        MutateNode(parent);
        parent.Children.Add(node);
        parent.Touch();
        return node;
    }

    private void CloseOpenChild(MdNode parent)
    {
        MdNode? child = LastOpenChild(parent);
        if (child is not null)
        {
            CloseNode(child);
        }
    }

    private void CloseNode(MdNode node)
    {
        CloseRecursive(node, closeSelf: true);
    }

    private void CloseRecursive(MdNode node, bool closeSelf)
    {
        MdNode? child = LastOpenChild(node);
        if (child is not null)
        {
            CloseRecursive(child, closeSelf: true);
        }

        if (!closeSelf || !node.IsOpen)
        {
            return;
        }

        MutateNode(node);

        if (node.Kind == BlockKind.IndentedCode)
        {
            while (node.Lines.Count > 0 && node.Lines[^1].Length == 0)
            {
                node.Lines.RemoveAt(node.Lines.Count - 1);
            }
        }

        node.IsOpen = false;
        node.Touch();
    }

    private void MarkEnclosingListLoose(MdNode container)
    {
        for (MdNode? node = container; node is not null; node = node.Parent)
        {
            if (node.Kind != BlockKind.List || !node.IsTight)
            {
                continue;
            }

            if (node.Children.Count > 0 && node.Children[^1].Children.Count > 0)
            {
                MutateNode(node);
                node.IsTight = false;
                node.Touch();
            }

            break;
        }
    }

    private void AddLine(MdNode node, SourceSpan span)
    {
        MutateNode(node);
        node.Lines.Add(span);
        node.Touch();
    }

    private ReadOnlySpan<char> LineText(SourceSpan span) => _buffer.Slice(span.Start, span.Length);

    private MdNode RentNode(BlockKind kind)
    {
        MdNode node;
        if (_recycleCursor < _recycleBin.Count && _recycleBin[_recycleCursor].Kind == kind)
        {
            node = _recycleBin[_recycleCursor++];
            node.ResetForReuse(kind);
        }
        else
        {
            node = new MdNode(kind);
        }

        node.ParseSequence = ++_parseSequence;
        return node;
    }

    // ------------------------------------------------------------------
    // Undo journal
    // ------------------------------------------------------------------

    private void MutateNode(MdNode node)
    {
        if (!_journalActive || node.JournalMark == _lineToken)
        {
            return;
        }

        node.JournalMark = _lineToken;
        _journal.Add(NodeSnapshot.Capture(node));
    }

    private readonly struct NodeSnapshot
    {
        private readonly MdNode _node;
        private readonly int _childCount;
        private readonly int _lineCount;
        private readonly int _version;
        private readonly BlockKind _kind;
        private readonly bool _isOpen;
        private readonly bool _isTight;
        private readonly bool _isUnterminated;
        private readonly int _headingLevel;
        private readonly int _contentIndent;
        private readonly string? _info;
        private readonly TableAlignment[]? _alignments;
        private readonly bool? _taskChecked;

        private NodeSnapshot(MdNode node)
        {
            _node = node;
            _childCount = node.Children.Count;
            _lineCount = node.Lines.Count;
            _version = node.Version;
            _kind = node.Kind;
            _isOpen = node.IsOpen;
            _isTight = node.IsTight;
            _isUnterminated = node.IsUnterminated;
            _headingLevel = node.HeadingLevel;
            _contentIndent = node.ContentIndent;
            _info = node.Info;
            _alignments = node.ColumnAlignments;
            _taskChecked = node.TaskChecked;
        }

        public static NodeSnapshot Capture(MdNode node) => new(node);

        public void Restore(List<MdNode> removed)
        {
            MdNode node = _node;

            for (int i = node.Children.Count - 1; i >= _childCount; i--)
            {
                MdNode child = node.Children[i];
                node.Children.RemoveAt(i);
                child.Parent = null;
                removed.Add(child);
            }

            if (node.Lines.Count > _lineCount)
            {
                node.Lines.RemoveRange(_lineCount, node.Lines.Count - _lineCount);
            }

            node.Version = _version;
            node.Kind = _kind;
            node.IsOpen = _isOpen;
            node.IsTight = _isTight;
            node.IsUnterminated = _isUnterminated;
            node.HeadingLevel = _headingLevel;
            node.ContentIndent = _contentIndent;
            node.Info = _info;
            node.ColumnAlignments = _alignments;
            node.TaskChecked = _taskChecked;
            node.JournalMark = -1;

            // A speculative pass may have populated caches under a version number that is now
            // being reused for different content, so they must be discarded.
            node.CachedInlines = null;
            node.InlineCacheVersion = -1;
            node.FlatCache = null;
            node.FlatCacheVersion = -1;
        }
    }

    internal readonly record struct ListMarkerInfo(bool IsOrdered, int Start, char Marker, int ContentIndent);
}
