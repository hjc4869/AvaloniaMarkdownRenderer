using System.Runtime.CompilerServices;

namespace AvaloniaMarkdown.Text;

/// <summary>
/// A region of the source document, expressed as an offset/length pair.
/// </summary>
public readonly struct SourceSpan : IEquatable<SourceSpan>
{
    public static readonly SourceSpan Empty = new(0, 0);

    public SourceSpan(int start, int length)
    {
        Start = start;
        Length = length;
    }

    public int Start { get; }

    public int Length { get; }

    public int End
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Start + Length;
    }

    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Length == 0;
    }

    public SourceSpan Slice(int offset) => new(Start + offset, Length - offset);

    public SourceSpan Slice(int offset, int length) => new(Start + offset, length);

    public bool Equals(SourceSpan other) => Start == other.Start && Length == other.Length;

    public override bool Equals(object? obj) => obj is SourceSpan other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Start, Length);

    public override string ToString() => $"[{Start}..{End})";
}
