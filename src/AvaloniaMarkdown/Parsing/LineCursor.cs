using AvaloniaMarkdown.Text;

namespace AvaloniaMarkdown.Parsing;

/// <summary>
/// A tab-aware cursor over a single source line.
/// </summary>
/// <remarks>
/// Tracks both the character index and the expanded <see cref="Column"/> so that
/// indentation rules (which are column based, with tab stops of four) can be applied
/// without materialising an expanded copy of the line.
/// </remarks>
internal ref struct LineCursor
{
    public const int TabSize = 4;

    private readonly ReadOnlySpan<char> _text;
    private readonly int _absoluteStart;
    private int _index;
    private int _column;

    public LineCursor(ReadOnlySpan<char> text, int absoluteStart)
    {
        _text = text;
        _absoluteStart = absoluteStart;
        _index = 0;
        _column = 0;
    }

    public readonly int Index => _index;

    public readonly int Column => _column;

    public readonly int Length => _text.Length;

    public readonly bool AtEnd => _index >= _text.Length;

    public readonly char Current => _text[_index];

    public readonly ReadOnlySpan<char> Text => _text;

    public readonly ReadOnlySpan<char> Remaining => _text[_index..];

    public readonly SourceSpan RemainingSpan => new(_absoluteStart + _index, _text.Length - _index);

    public readonly SourceSpan SpanFrom(int index) => new(_absoluteStart + index, _text.Length - index);

    public readonly SourceSpan SpanOf(int index, int length) => new(_absoluteStart + index, length);

    public readonly char PeekAt(int offset)
    {
        int i = _index + offset;
        return (uint)i < (uint)_text.Length ? _text[i] : '\0';
    }

    /// <summary>True when the remainder of the line contains only whitespace.</summary>
    public readonly bool IsBlank
    {
        get
        {
            for (int i = _index; i < _text.Length; i++)
            {
                char c = _text[i];
                if (c != ' ' && c != '\t')
                {
                    return false;
                }
            }

            return true;
        }
    }

    public void Advance(int count = 1)
    {
        for (int i = 0; i < count && _index < _text.Length; i++)
        {
            _column += _text[_index] == '\t' ? TabSize - (_column % TabSize) : 1;
            _index++;
        }
    }

    /// <summary>Number of indentation columns available at the current position.</summary>
    public readonly int PeekIndent()
    {
        int column = _column;
        int i = _index;
        while (i < _text.Length)
        {
            char c = _text[i];
            if (c == ' ')
            {
                column++;
            }
            else if (c == '\t')
            {
                column += TabSize - (column % TabSize);
            }
            else
            {
                break;
            }

            i++;
        }

        return column - _column;
    }

    /// <summary>Consumes at most <paramref name="maxColumns"/> columns of leading whitespace.</summary>
    public void SkipIndent(int maxColumns)
    {
        int target = _column + maxColumns;
        while (_index < _text.Length && _column < target)
        {
            char c = _text[_index];
            if (c != ' ' && c != '\t')
            {
                break;
            }

            int next = c == '\t' ? _column + (TabSize - (_column % TabSize)) : _column + 1;
            if (next > target)
            {
                break;
            }

            _column = next;
            _index++;
        }
    }

    public void SkipWhitespace()
    {
        while (_index < _text.Length && (_text[_index] == ' ' || _text[_index] == '\t'))
        {
            Advance();
        }
    }

    public readonly CursorState Save() => new(_index, _column);

    public void Restore(CursorState state)
    {
        _index = state.Index;
        _column = state.Column;
    }

    internal readonly record struct CursorState(int Index, int Column);
}
