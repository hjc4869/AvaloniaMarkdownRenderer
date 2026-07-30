using AvaloniaMarkdown.Flattening;

namespace AvaloniaMarkdown.Diffing;

/// <summary>The kind of change the view layer must apply.</summary>
public enum RenderOperationKind
{
    /// <summary>A new block was added at the end of the document.</summary>
    AppendBlock,

    /// <summary>A new block was added in the middle of the document.</summary>
    InsertBlock,

    /// <summary>A block changed kind or identity and its control must be rebuilt.</summary>
    ReplaceBlock,

    /// <summary>The block kept its identity and kind; the realised control can update in place.</summary>
    UpdateInline,

    /// <summary>A block disappeared.</summary>
    RemoveBlock,

    /// <summary>A block stopped streaming and reached its final content.</summary>
    FinalizeBlock,
}

/// <summary>A single instruction produced by <see cref="BlockDiffEngine"/>.</summary>
public readonly struct RenderOperation
{
    public RenderOperation(RenderOperationKind kind, int index, FlatBlock? block)
    {
        Kind = kind;
        Index = index;
        Block = block;
    }

    public RenderOperationKind Kind { get; }

    /// <summary>Index into the new snapshot (or, for removals, into the old one).</summary>
    public int Index { get; }

    public FlatBlock? Block { get; }

    public override string ToString() => $"{Kind}@{Index} {Block}";
}

/// <summary>
/// Computes the minimal set of view mutations between two snapshots.
/// </summary>
/// <remarks>
/// <para>
/// Snapshots from the same generation share their frozen prefix by reference, so the comparison
/// can start at the smaller of the two <see cref="MarkdownSnapshot.StableCount"/> values instead
/// of index zero. For an append-only stream this means the diff inspects a handful of entries no
/// matter how large the document is.
/// </para>
/// <para>
/// The engine never emits a "clear everything" instruction: even a full document replacement is
/// expressed as a keyed prefix/suffix diff so unchanged controls survive.
/// </para>
/// </remarks>
public sealed class BlockDiffEngine
{
    private readonly List<RenderOperation> _operations = new();

    /// <summary>Diffs <paramref name="previous"/> into <paramref name="current"/>.</summary>
    public IReadOnlyList<RenderOperation> Diff(MarkdownSnapshot previous, MarkdownSnapshot current)
    {
        _operations.Clear();

        int oldCount = previous.Count;
        int newCount = current.Count;

        int start = 0;
        if (previous.SharesPrefixWith(current))
        {
            start = Math.Min(previous.StableCount, current.StableCount);
        }

        // Common prefix.
        int prefix = start;
        while (prefix < oldCount && prefix < newCount && IsIdentical(previous[prefix], current[prefix]))
        {
            prefix++;
        }

        if (prefix == oldCount && prefix == newCount)
        {
            return _operations;
        }

        // Common suffix (never overlapping the prefix).
        int suffix = 0;
        while (suffix < oldCount - prefix &&
               suffix < newCount - prefix &&
               IsIdentical(previous[oldCount - 1 - suffix], current[newCount - 1 - suffix]))
        {
            suffix++;
        }

        int oldMiddle = oldCount - suffix;
        int newMiddle = newCount - suffix;

        int i = prefix;
        int j = prefix;

        while (i < oldMiddle && j < newMiddle)
        {
            FlatBlock oldBlock = previous[i];
            FlatBlock newBlock = current[j];

            if (oldBlock.Key == newBlock.Key)
            {
                if (oldBlock.Kind == newBlock.Kind)
                {
                    _operations.Add(new RenderOperation(RenderOperationKind.UpdateInline, j, newBlock));
                }
                else
                {
                    _operations.Add(new RenderOperation(RenderOperationKind.ReplaceBlock, j, newBlock));
                }

                if (oldBlock.IsOpen && !newBlock.IsOpen)
                {
                    _operations.Add(new RenderOperation(RenderOperationKind.FinalizeBlock, j, newBlock));
                }

                i++;
                j++;
                continue;
            }

            // Keys diverge: replace positionally for as long as both sides have entries.
            _operations.Add(new RenderOperation(RenderOperationKind.ReplaceBlock, j, newBlock));
            i++;
            j++;
        }

        // Trailing removals from the old snapshot (emitted back-to-front so indices stay valid).
        for (int k = oldMiddle - 1; k >= i; k--)
        {
            _operations.Add(new RenderOperation(RenderOperationKind.RemoveBlock, k, null));
        }

        // Trailing additions.
        for (; j < newMiddle; j++)
        {
            RenderOperationKind kind = suffix > 0 ? RenderOperationKind.InsertBlock : RenderOperationKind.AppendBlock;
            _operations.Add(new RenderOperation(kind, j, current[j]));
        }

        return _operations;
    }

    private static bool IsIdentical(FlatBlock a, FlatBlock b) =>
        ReferenceEquals(a, b) || (a.Key == b.Key && a.Version == b.Version && a.Kind == b.Kind);
}
