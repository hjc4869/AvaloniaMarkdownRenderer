using AvaloniaMarkdown.Ast;

namespace AvaloniaMarkdown.Parsing;

/// <summary>Helpers for GFM pipe tables.</summary>
internal static class TableParser
{
    /// <summary>
    /// Splits a table row on unescaped pipes, writing cell spans (relative to <paramref name="line"/>)
    /// into <paramref name="cells"/>. Returns the number of cells.
    /// </summary>
    public static int SplitRow(ReadOnlySpan<char> line, Span<(int Start, int Length)> cells)
    {
        int start = 0;
        int end = line.Length;

        // Trim surrounding whitespace.
        while (start < end && (line[start] == ' ' || line[start] == '\t'))
        {
            start++;
        }

        while (end > start && (line[end - 1] == ' ' || line[end - 1] == '\t'))
        {
            end--;
        }

        // A leading pipe is a delimiter, not an empty first cell.
        if (start < end && line[start] == '|')
        {
            start++;
        }

        // Same for a trailing (unescaped) pipe.
        if (end > start && line[end - 1] == '|' && (end - 2 < start || line[end - 2] != '\\'))
        {
            end--;
        }

        int count = 0;
        int cellStart = start;
        for (int i = start; i < end; i++)
        {
            char c = line[i];
            if (c == '\\')
            {
                i++;
                continue;
            }

            if (c != '|')
            {
                continue;
            }

            if (count < cells.Length)
            {
                cells[count] = Trim(line, cellStart, i - cellStart);
            }

            count++;
            cellStart = i + 1;
        }

        if (count < cells.Length)
        {
            cells[count] = Trim(line, cellStart, end - cellStart);
        }

        count++;
        return count;
    }

    public static int CountColumns(ReadOnlySpan<char> line) => SplitRow(line, Span<(int, int)>.Empty);

    /// <summary>Attempts to read a GFM delimiter row such as <c>| :--- | ---: |</c>.</summary>
    public static bool TryParseDelimiterRow(ReadOnlySpan<char> line, out TableAlignment[] alignments)
    {
        alignments = Array.Empty<TableAlignment>();

        int columns = CountColumns(line);
        if (columns <= 0 || columns > 512)
        {
            return false;
        }

        Span<(int Start, int Length)> cells = columns <= 64
            ? stackalloc (int, int)[columns]
            : new (int, int)[columns];

        SplitRow(line, cells);

        var result = new TableAlignment[columns];
        for (int i = 0; i < columns; i++)
        {
            ReadOnlySpan<char> cell = line.Slice(cells[i].Start, cells[i].Length);
            if (!TryParseAlignment(cell, out result[i]))
            {
                return false;
            }
        }

        alignments = result;
        return true;
    }

    private static bool TryParseAlignment(ReadOnlySpan<char> cell, out TableAlignment alignment)
    {
        alignment = TableAlignment.None;
        if (cell.IsEmpty)
        {
            return false;
        }

        int i = 0;
        bool left = false;
        bool right = false;

        if (cell[i] == ':')
        {
            left = true;
            i++;
        }

        int dashes = 0;
        while (i < cell.Length && cell[i] == '-')
        {
            dashes++;
            i++;
        }

        if (dashes == 0)
        {
            return false;
        }

        if (i < cell.Length && cell[i] == ':')
        {
            right = true;
            i++;
        }

        if (i != cell.Length)
        {
            return false;
        }

        alignment = (left, right) switch
        {
            (true, true) => TableAlignment.Center,
            (true, false) => TableAlignment.Left,
            (false, true) => TableAlignment.Right,
            _ => TableAlignment.None,
        };

        return true;
    }

    private static (int Start, int Length) Trim(ReadOnlySpan<char> line, int start, int length)
    {
        int end = start + length;
        while (start < end && (line[start] == ' ' || line[start] == '\t'))
        {
            start++;
        }

        while (end > start && (line[end - 1] == ' ' || line[end - 1] == '\t'))
        {
            end--;
        }

        return (start, end - start);
    }
}
