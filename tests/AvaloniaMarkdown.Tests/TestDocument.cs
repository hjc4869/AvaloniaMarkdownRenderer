using AvaloniaMarkdown;
using AvaloniaMarkdown.Ast;
using AvaloniaMarkdown.Flattening;

namespace AvaloniaMarkdown.Tests;

/// <summary>Shared helpers for driving a document deterministically inside tests.</summary>
internal static class TestDocument
{
    /// <summary>Creates a synchronous document so assertions never race the worker thread.</summary>
    public static MarkdownDocument Create(MarkdownOptions? options = null) =>
        new(options ?? MarkdownOptions.Default, MarkdownProcessingMode.Inline);

    /// <summary>Parses <paramref name="markdown"/> in one shot and closes every open block.</summary>
    public static MarkdownSnapshot Parse(string markdown, MarkdownOptions? options = null)
    {
        MarkdownDocument document = Create(options);
        document.Append(markdown);
        document.Complete();
        return document.Snapshot;
    }

    /// <summary>Feeds <paramref name="markdown"/> one character at a time, simulating a token stream.</summary>
    public static MarkdownDocument Stream(string markdown, MarkdownOptions? options = null)
    {
        MarkdownDocument document = Create(options);
        foreach (char c in markdown)
        {
            document.Append(c.ToString());
        }

        return document;
    }

    /// <summary>Feeds <paramref name="markdown"/> in fixed-size chunks.</summary>
    public static MarkdownDocument StreamChunks(string markdown, int chunkSize, MarkdownOptions? options = null)
    {
        MarkdownDocument document = Create(options);
        for (int i = 0; i < markdown.Length; i += chunkSize)
        {
            document.Append(markdown.Substring(i, Math.Min(chunkSize, markdown.Length - i)));
        }

        return document;
    }

    /// <summary>Renders a snapshot into a compact, assertable text form.</summary>
    public static string Describe(MarkdownSnapshot snapshot)
    {
        var lines = new List<string>(snapshot.Count);
        foreach (FlatBlock block in snapshot)
        {
            string content = block.Kind switch
            {
                FlatBlockKind.Code => block.CodeText ?? string.Empty,
                FlatBlockKind.Image => $"{block.ImageAlt}|{block.ImageUrl}",
                FlatBlockKind.Table => DescribeTable(block),
                FlatBlockKind.ThematicBreak => string.Empty,
                _ => block.Inlines.Text,
            };

            string marker = block.Marker is null ? string.Empty : $"[{block.Marker}]";
            string task = block.TaskChecked is null ? string.Empty : block.TaskChecked.Value ? "[x]" : "[ ]";
            lines.Add($"{block.Kind}{(block.Kind == FlatBlockKind.Heading ? block.HeadingLevel.ToString() : string.Empty)}" +
                      $" q{block.QuoteDepth} i{block.IndentLevel} {marker}{task} {content.Replace("\n", "\\n")}".TrimEnd());
        }

        return string.Join('\n', lines);
    }

    private static string DescribeTable(FlatBlock block)
    {
        TableModel table = block.Table!;
        var parts = new List<string>();
        parts.Add(string.Join('|', table.Header.Select(c => c.Text)));
        foreach (InlineContent[] row in table.Rows)
        {
            parts.Add(string.Join('|', row.Select(c => c.Text)));
        }

        return string.Join(" / ", parts);
    }

    /// <summary>Returns the styles applied to each character of a block's inline content.</summary>
    public static string StyleMap(Ast.InlineContent content)
    {
        Span<char> map = content.Text.Length <= 256 ? stackalloc char[content.Text.Length] : new char[content.Text.Length];
        map.Fill('.');

        foreach (Ast.InlineRun run in content.Runs)
        {
            char symbol = run.Style switch
            {
                var s when (s & Ast.InlineStyle.Code) != 0 => 'c',
                var s when (s & Ast.InlineStyle.Bold) != 0 && (s & Ast.InlineStyle.Italic) != 0 => 'X',
                var s when (s & Ast.InlineStyle.Bold) != 0 => 'b',
                var s when (s & Ast.InlineStyle.Italic) != 0 => 'i',
                var s when (s & Ast.InlineStyle.Strikethrough) != 0 => 's',
                var s when (s & Ast.InlineStyle.Link) != 0 => 'l',
                var s when (s & Ast.InlineStyle.Image) != 0 => 'g',
                _ => '.',
            };

            for (int i = run.Start; i < run.End && i < map.Length; i++)
            {
                map[i] = symbol;
            }
        }

        return new string(map);
    }
}
