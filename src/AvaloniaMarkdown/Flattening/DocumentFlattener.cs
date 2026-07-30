using System.Text;
using AvaloniaMarkdown.Ast;
using AvaloniaMarkdown.Parsing;
using AvaloniaMarkdown.Text;

namespace AvaloniaMarkdown.Flattening;

/// <summary>
/// Projects the block tree onto the linear list of render items consumed by the view layer.
/// </summary>
/// <remarks>
/// <para>
/// Flattening is incremental. Each container remembers how many of its leading children have
/// already been promoted into the shared frozen prefix, so a pass only walks the open path
/// (root → … → tip) plus whatever closed since the previous pass. For an append-only stream that
/// is one or two nodes, independent of document size.
/// </para>
/// <para>
/// Promotion only happens on committed state. While a speculative (partial) line is applied,
/// blocks are produced into the volatile tail and can be discarded without touching the frozen
/// prefix.
/// </para>
/// </remarks>
public sealed class DocumentFlattener
{
    private static readonly string[] BulletGlyphs = { "\u2022", "\u25e6", "\u25aa" };

    private readonly TextBuffer _buffer;
    private readonly MarkdownOptions _options;
    private readonly InlineParser _inline = new();
    private readonly StringBuilder _scratch = new();
    private readonly List<FlatBlock> _tail = new();
    private readonly List<(MdNode Node, int Index)> _containers = new();
    private readonly List<FlatBlock> _codeScratch = new();

    private FrozenBlockList _frozen = new();
    private int _generation;
    private long _version;

    private int _promotable;
    private bool _boundarySet;
    private bool _freezeAllowed;
    private string? _pendingMarker;
    private bool? _pendingTask;
    private bool _listTight = true;

    public DocumentFlattener(TextBuffer buffer, MarkdownOptions options)
    {
        _buffer = buffer;
        _options = options;
    }

    /// <summary>Starts a new generation; existing snapshots stay valid but stop sharing a prefix.</summary>
    public void Reset()
    {
        _frozen = new FrozenBlockList();
        _generation++;
        _tail.Clear();
    }

    /// <summary>
    /// Produces a snapshot for the current tree.
    /// </summary>
    /// <param name="root">Document root.</param>
    /// <param name="promote">
    /// True when the tree reflects only committed lines, allowing closed blocks to be moved into
    /// the permanent prefix.
    /// </param>
    public MarkdownSnapshot Flatten(MdNode root, bool promote)
    {
        _tail.Clear();
        _containers.Clear();
        _promotable = 0;
        _boundarySet = false;
        _freezeAllowed = true;
        _pendingMarker = null;
        _pendingTask = null;
        _listTight = true;

        VisitChildren(root);

        if (!_boundarySet)
        {
            _promotable = _tail.Count;
        }

        if (promote && _promotable > 0)
        {
            for (int i = 0; i < _promotable; i++)
            {
                _frozen.Add(_tail[i]);
            }

            _tail.RemoveRange(0, _promotable);

            foreach ((MdNode node, int index) in _containers)
            {
                int previousIndex = node.FlatFrozenChildIndex;
                if (index <= previousIndex)
                {
                    continue;
                }

                node.FlatFrozenChildIndex = index;

                // Everything below the new watermark is immutable and will never be flattened
                // again, so its parse-time state can be released.
                for (int i = previousIndex; i < index; i++)
                {
                    node.Children[i].ReleaseRetainedState();
                }
            }
        }

        FlatBlock[] tail = _tail.Count == 0 ? Array.Empty<FlatBlock>() : _tail.ToArray();
        return new MarkdownSnapshot(_frozen, _frozen.Count, tail, _generation, ++_version);
    }

    // ------------------------------------------------------------------
    // Traversal
    // ------------------------------------------------------------------

    private void Visit(MdNode node)
    {
        switch (node.Kind)
        {
            case BlockKind.Document:
            case BlockKind.BlockQuote:
            case BlockKind.ListItem:
                VisitChildren(node);
                return;

            case BlockKind.List:
                VisitList(node);
                return;

            default:
                EmitLeaf(node);
                return;
        }
    }

    private void VisitChildren(MdNode node)
    {
        RecordContainer(node);

        for (int i = node.FlatFrozenChildIndex; i < node.Children.Count; i++)
        {
            Visit(node.Children[i]);
        }
    }

    private void VisitList(MdNode list)
    {
        // A list's tightness can still flip while it is open, which would invalidate already
        // frozen items, so nothing inside an open list is ever promoted.
        if (list.IsOpen)
        {
            SetBoundary();
            _freezeAllowed = false;
        }

        RecordContainer(list);

        bool previousTight = _listTight;
        _listTight = list.IsTight;

        for (int i = list.FlatFrozenChildIndex; i < list.Children.Count; i++)
        {
            MdNode item = list.Children[i];
            _pendingMarker = BuildMarker(list, i);
            _pendingTask = _options.EnableTaskLists ? item.TaskChecked : null;
            Visit(item);
        }

        _pendingMarker = null;
        _pendingTask = null;
        _listTight = previousTight;
    }

    private void RecordContainer(MdNode node)
    {
        if (!_freezeAllowed)
        {
            return;
        }

        int index = node.Children.Count;
        if (index > 0 && node.Children[index - 1].IsOpen)
        {
            index--;
        }

        _containers.Add((node, index));
    }

    private void SetBoundary()
    {
        if (!_boundarySet)
        {
            _promotable = _tail.Count;
            _boundarySet = true;
        }
    }

    private string BuildMarker(MdNode list, int itemIndex)
    {
        if (!list.IsOrdered)
        {
            return BulletGlyphs[Math.Min(list.ListDepth, BulletGlyphs.Length - 1)];
        }

        _scratch.Clear();
        _scratch.Append(list.ListStart + itemIndex);
        _scratch.Append(list.ListMarker == ')' ? ')' : '.');
        return _scratch.ToString();
    }

    // ------------------------------------------------------------------
    // Leaf emission
    // ------------------------------------------------------------------

    private void EmitLeaf(MdNode node)
    {
        if (node.IsOpen)
        {
            SetBoundary();
            _freezeAllowed = false;
        }

        string? marker = _pendingMarker;
        bool? task = _pendingTask;
        _pendingMarker = null;
        _pendingTask = null;

        if (node.FlatCacheVersion == node.Version &&
            node.FlatCache is FlatBlock[] cached &&
            cached.Length > 0 &&
            cached[0].Marker == marker &&
            cached[0].IsTightList == _listTight)
        {
            _tail.AddRange(cached);
            return;
        }

        int start = _tail.Count;

        switch (node.Kind)
        {
            case BlockKind.Paragraph:
                EmitParagraph(node, marker, task);
                break;

            case BlockKind.Heading:
                EmitHeading(node, marker, task);
                break;

            case BlockKind.FencedCode:
            case BlockKind.IndentedCode:
                EmitCode(node, marker, task);
                break;

            case BlockKind.ThematicBreak:
                _tail.Add(new FlatBlock
                {
                    BlockId = node.Id,
                    Version = node.Version,
                    Kind = FlatBlockKind.ThematicBreak,
                    QuoteDepth = node.QuoteDepth,
                    IndentLevel = node.ListDepth,
                    IsOpen = node.IsOpen,
                    Marker = marker,
                    TaskChecked = task,
                    IsTightList = _listTight,
                });
                break;

            case BlockKind.Table:
                EmitTable(node, marker, task);
                break;

            case BlockKind.HtmlBlock:
                EmitHtml(node, marker, task);
                break;
        }

        int count = _tail.Count - start;
        var produced = new FlatBlock[count];
        _tail.CopyTo(start, produced, 0, count);
        node.FlatCache = produced;
        node.FlatCacheVersion = node.Version;
    }

    private void EmitParagraph(MdNode node, string? marker, bool? task)
    {
        string text = JoinTextLines(node);
        InlineContent inlines = ParseInlines(node, text);

        if (TryEmitImageBlocks(node, inlines, marker, task))
        {
            return;
        }

        _tail.Add(new FlatBlock
        {
            BlockId = node.Id,
            Version = node.Version,
            Kind = FlatBlockKind.Paragraph,
            QuoteDepth = node.QuoteDepth,
            IndentLevel = node.ListDepth,
            IsOpen = node.IsOpen,
            Marker = marker,
            TaskChecked = task,
            IsTightList = _listTight,
            Inlines = inlines,
        });
    }

    private void EmitHeading(MdNode node, string? marker, bool? task)
    {
        string text = JoinTextLines(node);
        InlineContent inlines = ParseInlines(node, text);

        _tail.Add(new FlatBlock
        {
            BlockId = node.Id,
            Version = node.Version,
            Kind = FlatBlockKind.Heading,
            HeadingLevel = Math.Clamp(node.HeadingLevel, 1, 6),
            QuoteDepth = node.QuoteDepth,
            IndentLevel = node.ListDepth,
            IsOpen = node.IsOpen,
            Marker = marker,
            TaskChecked = task,
            IsTightList = _listTight,
            Inlines = inlines,
        });
    }

    private void EmitHtml(MdNode node, string? marker, bool? task)
    {
        string text = JoinTextLines(node);
        InlineContent inlines = ParseInlines(node, text);

        _tail.Add(new FlatBlock
        {
            BlockId = node.Id,
            Version = node.Version,
            Kind = FlatBlockKind.Html,
            QuoteDepth = node.QuoteDepth,
            IndentLevel = node.ListDepth,
            IsOpen = node.IsOpen,
            Marker = marker,
            TaskChecked = task,
            IsTightList = _listTight,
            Inlines = inlines,
        });
    }

    private bool TryEmitImageBlocks(MdNode node, InlineContent inlines, string? marker, bool? task)
    {
        if (inlines.Targets.Length == 0 || inlines.Runs.Length == 0)
        {
            return false;
        }

        bool sawImage = false;
        foreach (InlineRun run in inlines.Runs)
        {
            bool isImage = (run.Style & InlineStyle.Image) != 0 && run.TargetId >= 0;
            if (isImage)
            {
                sawImage = true;
                continue;
            }

            if (!IsWhitespace(inlines.Text.AsSpan(run.Start, run.Length)))
            {
                return false;
            }
        }

        if (!sawImage)
        {
            return false;
        }

        int segment = 0;
        int lastTarget = -1;
        foreach (InlineRun run in inlines.Runs)
        {
            if ((run.Style & InlineStyle.Image) == 0 || run.TargetId < 0 || run.TargetId == lastTarget)
            {
                continue;
            }

            lastTarget = run.TargetId;
            InlineTarget target = inlines.Targets[run.TargetId];
            if (!UriSafety.IsAllowedImageUrl(target.Url))
            {
                continue;
            }

            _tail.Add(new FlatBlock
            {
                BlockId = node.Id,
                SegmentIndex = segment++,
                Version = node.Version,
                Kind = FlatBlockKind.Image,
                QuoteDepth = node.QuoteDepth,
                IndentLevel = node.ListDepth,
                IsOpen = node.IsOpen,
                Marker = segment == 1 ? marker : null,
                TaskChecked = segment == 1 ? task : null,
                IsTightList = _listTight,
                ImageUrl = target.Url,
                ImageAlt = inlines.Text.Substring(run.Start, run.Length),
                ImageTitle = target.Title,
            });
        }

        return segment > 0;
    }

    private void EmitCode(MdNode node, string? marker, bool? task)
    {
        int lineCount = node.Lines.Count;
        int chunkLines = Math.Max(8, _options.CodeBlockChunkLines);
        int segments = Math.Max(1, (lineCount + chunkLines - 1) / chunkLines);

        var state = node.CachedCodeState ??= new CodeBlockState();

        // Code blocks only ever grow, so every segment except the last is final and can be
        // reused verbatim between passes.
        _codeScratch.Clear();
        if (node.FlatCache is FlatBlock[] previous && previous.Length > 0 && ReferenceEquals(previous[0].CodeState, state))
        {
            int reusable = Math.Min(previous.Length - 1, segments - 1);
            for (int i = 0; i < reusable; i++)
            {
                _codeScratch.Add(previous[i]);
            }
        }

        for (int segment = _codeScratch.Count; segment < segments; segment++)
        {
            int from = segment * chunkLines;
            int to = Math.Min(lineCount, from + chunkLines);

            _scratch.Clear();
            for (int i = from; i < to; i++)
            {
                if (i > from)
                {
                    _scratch.Append('\n');
                }

                SourceSpan span = node.Lines[i];
                _scratch.Append(_buffer.Slice(span.Start, span.Length));
            }

            CodeSegmentRole role = segments == 1
                ? CodeSegmentRole.Only
                : segment == 0
                    ? CodeSegmentRole.First
                    : segment == segments - 1
                        ? CodeSegmentRole.Last
                        : CodeSegmentRole.Middle;

            _codeScratch.Add(new FlatBlock
            {
                BlockId = node.Id,
                SegmentIndex = segment,
                Version = node.Version,
                Kind = FlatBlockKind.Code,
                QuoteDepth = node.QuoteDepth,
                IndentLevel = node.ListDepth,
                IsOpen = node.IsOpen,
                Marker = segment == 0 ? marker : null,
                TaskChecked = segment == 0 ? task : null,
                IsTightList = _listTight,
                CodeText = _scratch.ToString(),
                Language = node.Info is null ? null : ExtractLanguage(node.Info),
                FirstLineNumber = from + 1,
                LineCount = to - from,
                TotalLineCount = lineCount,
                SegmentRole = role,
                CodeState = state,
            });
        }

        _tail.AddRange(_codeScratch);
    }

    private void EmitTable(MdNode node, string? marker, bool? task)
    {
        TableAlignment[] alignments = node.ColumnAlignments ?? Array.Empty<TableAlignment>();
        int columns = alignments.Length;

        InlineContent[] header = node.Lines.Count > 0
            ? ParseRow(node.Lines[0], columns)
            : Array.Empty<InlineContent>();

        int rowCount = Math.Max(0, node.Lines.Count - 1);
        var rows = new InlineContent[rowCount][];
        for (int i = 0; i < rowCount; i++)
        {
            rows[i] = ParseRow(node.Lines[i + 1], columns);
        }

        _tail.Add(new FlatBlock
        {
            BlockId = node.Id,
            Version = node.Version,
            Kind = FlatBlockKind.Table,
            QuoteDepth = node.QuoteDepth,
            IndentLevel = node.ListDepth,
            IsOpen = node.IsOpen,
            Marker = marker,
            TaskChecked = task,
            IsTightList = _listTight,
            Table = new TableModel(header, rows, alignments),
        });
    }

    private InlineContent[] ParseRow(SourceSpan lineSpan, int columns)
    {
        ReadOnlySpan<char> line = _buffer.Slice(lineSpan.Start, lineSpan.Length);
        int actual = TableParser.CountColumns(line);
        Span<(int Start, int Length)> cells = actual <= 64 ? stackalloc (int, int)[actual] : new (int, int)[actual];
        TableParser.SplitRow(line, cells);

        var result = new InlineContent[columns];
        for (int i = 0; i < columns; i++)
        {
            if (i < actual)
            {
                string text = new(line.Slice(cells[i].Start, cells[i].Length));
                result[i] = _inline.Parse(UnescapePipes(text), _options);
            }
            else
            {
                result[i] = InlineContent.Empty;
            }
        }

        return result;
    }

    private static string UnescapePipes(string text) =>
        text.Contains("\\|", StringComparison.Ordinal) ? text.Replace("\\|", "|", StringComparison.Ordinal) : text;

    // ------------------------------------------------------------------
    // Text helpers
    // ------------------------------------------------------------------

    private InlineContent ParseInlines(MdNode node, string text)
    {
        if (node.InlineCacheVersion == node.Version && node.CachedInlines is { } cached)
        {
            return cached;
        }

        InlineContent content = _inline.Parse(text, _options, autoCloseTrailing: node.IsOpen);
        node.CachedInlines = content;
        node.InlineCacheVersion = node.Version;
        return content;
    }

    private string JoinTextLines(MdNode node)
    {
        _scratch.Clear();

        for (int i = 0; i < node.Lines.Count; i++)
        {
            SourceSpan span = node.Lines[i];
            ReadOnlySpan<char> line = _buffer.Slice(span.Start, span.Length);

            int end = line.Length;
            while (end > 0 && (line[end - 1] == ' ' || line[end - 1] == '\t' || line[end - 1] == '\r'))
            {
                end--;
            }

            // A trailing backslash is an explicit hard break.
            if (end > 0 && line[end - 1] == '\\' && (end < 2 || line[end - 2] != '\\'))
            {
                end--;
            }

            int begin = 0;
            if (i > 0)
            {
                while (begin < end && (line[begin] == ' ' || line[begin] == '\t'))
                {
                    begin++;
                }

                _scratch.Append('\n');
            }

            _scratch.Append(line[begin..end]);
        }

        return _scratch.ToString();
    }

    private static string ExtractLanguage(string info)
    {
        int space = info.IndexOf(' ');
        return space < 0 ? info : info[..space];
    }

    private static bool IsWhitespace(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (!char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }
}
