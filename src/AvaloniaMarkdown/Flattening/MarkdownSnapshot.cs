using System.Collections;

namespace AvaloniaMarkdown.Flattening;

/// <summary>
/// An immutable view of the document at one instant, composed of a shared frozen prefix and a
/// small volatile tail.
/// </summary>
/// <remarks>
/// Snapshots are cheap: publishing one allocates only the tail array. Two snapshots from the same
/// document generation share their frozen prefix by reference, which lets the diff engine skip
/// straight to <see cref="StableCount"/> instead of comparing from index zero.
/// </remarks>
public sealed class MarkdownSnapshot : IReadOnlyList<FlatBlock>
{
    /// <summary>An empty snapshot in its own generation.</summary>
    public static readonly MarkdownSnapshot Empty = new(new FrozenBlockList(), 0, Array.Empty<FlatBlock>(), 0, 0);

    private readonly FrozenBlockList _frozen;
    private readonly int _frozenCount;
    private readonly FlatBlock[] _tail;

    internal MarkdownSnapshot(FrozenBlockList frozen, int frozenCount, FlatBlock[] tail, int generation, long version)
    {
        _frozen = frozen;
        _frozenCount = frozenCount;
        _tail = tail;
        Generation = generation;
        Version = version;
    }

    /// <summary>Incremented whenever the document is reset or replaced wholesale.</summary>
    public int Generation { get; }

    /// <summary>Monotonic publish counter.</summary>
    public long Version { get; }

    /// <summary>Number of leading blocks that can never change again.</summary>
    public int StableCount => _frozenCount;

    public int Count => _frozenCount + _tail.Length;

    public FlatBlock this[int index] =>
        index < _frozenCount ? _frozen[index] : _tail[index - _frozenCount];

    /// <summary>True when both snapshots were produced by the same document generation.</summary>
    public bool SharesPrefixWith(MarkdownSnapshot other) => ReferenceEquals(_frozen, other._frozen);

    public IEnumerator<FlatBlock> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
