using System.Buffers;
using System.Runtime.CompilerServices;

namespace AvaloniaMarkdown.Text;

/// <summary>
/// A growable, append-only character buffer that backs the streaming markdown document.
/// </summary>
/// <remarks>
/// <para>
/// The buffer keeps the whole document in a single contiguous <see cref="char"/> array so that
/// the parser can slice arbitrary regions as <see cref="ReadOnlySpan{T}"/> without allocating.
/// Growth is amortised (capacity doubling) and backed by <see cref="ArrayPool{T}"/>, so a
/// 1M character document costs ~2 MB and at most log2(n) copies over its lifetime.
/// </para>
/// <para>
/// Instances are <b>not</b> thread-safe. The buffer is owned exclusively by the parser thread;
/// rendering data is materialised into immutable snapshots before crossing thread boundaries.
/// </para>
/// </remarks>
public sealed class TextBuffer
{
    private const int MinimumCapacity = 1024;

    private char[] _buffer;
    private int _length;

    public TextBuffer(int initialCapacity = MinimumCapacity)
    {
        _buffer = ArrayPool<char>.Shared.Rent(Math.Max(MinimumCapacity, initialCapacity));
        _length = 0;
    }

    /// <summary>Number of characters currently stored.</summary>
    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _length;
    }

    /// <summary>Current backing array capacity. Exposed for diagnostics and tests.</summary>
    public int Capacity => _buffer.Length;

    public char this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer[index];
    }

    /// <summary>Appends a chunk of streamed text.</summary>
    public void Append(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return;
        }

        EnsureCapacity(_length + text.Length);
        text.CopyTo(_buffer.AsSpan(_length));
        _length += text.Length;
    }

    /// <summary>Returns a non-allocating view over a region of the document.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<char> Slice(int start, int length) => _buffer.AsSpan(start, length);

    /// <summary>Returns a non-allocating view from <paramref name="start"/> to the end.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<char> SliceToEnd(int start) => _buffer.AsSpan(start, _length - start);

    /// <summary>Materialises a region as a <see cref="string"/>. Only used when crossing to the UI thread.</summary>
    public string Substring(int start, int length) => length == 0 ? string.Empty : new string(_buffer, start, length);

    /// <summary>Finds the next occurrence of <paramref name="value"/> at or after <paramref name="start"/>, or -1.</summary>
    public int IndexOf(char value, int start)
    {
        if (start >= _length)
        {
            return -1;
        }

        int relative = _buffer.AsSpan(start, _length - start).IndexOf(value);
        return relative < 0 ? -1 : start + relative;
    }

    /// <summary>Drops all content but keeps the rented array for reuse.</summary>
    public void Clear() => _length = 0;

    /// <summary>Truncates the buffer to <paramref name="length"/> characters.</summary>
    public void Truncate(int length)
    {
        if ((uint)length > (uint)_length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _length = length;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
        {
            return;
        }

        int newCapacity = _buffer.Length;
        while (newCapacity < required)
        {
            newCapacity = newCapacity >= 0x2000_0000 ? required : newCapacity * 2;
        }

        char[] next = ArrayPool<char>.Shared.Rent(newCapacity);
        _buffer.AsSpan(0, _length).CopyTo(next);
        ArrayPool<char>.Shared.Return(_buffer);
        _buffer = next;
    }
}
