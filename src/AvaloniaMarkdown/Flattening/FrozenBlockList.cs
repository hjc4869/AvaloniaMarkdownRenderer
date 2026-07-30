namespace AvaloniaMarkdown.Flattening;

/// <summary>
/// Append-only, chunked list of permanently frozen render items.
/// </summary>
/// <remarks>
/// <para>
/// Once a block is closed it can never change, so its flattened output is appended here and then
/// shared — by reference — with every subsequent snapshot. That is what keeps publishing a
/// snapshot O(changed blocks) rather than O(document) and lets a 100k block document stream
/// without copying anything per token.
/// </para>
/// <para>
/// A single writer (the parser thread) appends; any number of readers may index elements below
/// the published <see cref="Count"/>. Chunk arrays are allocated full-size and never resized, and
/// <see cref="Count"/> is published with a volatile write after the element store, so readers
/// observe fully constructed entries without locking.
/// </para>
/// </remarks>
public sealed class FrozenBlockList
{
    private const int ChunkShift = 9;
    private const int ChunkSize = 1 << ChunkShift;
    private const int ChunkMask = ChunkSize - 1;

    private FlatBlock[]?[] _chunks = new FlatBlock[4][];
    private volatile int _count;

    public int Count => _count;

    public FlatBlock this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            FlatBlock[]?[] chunks = Volatile.Read(ref _chunks);
            return chunks[index >> ChunkShift]![index & ChunkMask];
        }
    }

    public void Add(FlatBlock block)
    {
        int index = _count;
        int chunkIndex = index >> ChunkShift;

        FlatBlock[]?[] chunks = _chunks;
        if (chunkIndex >= chunks.Length)
        {
            var grown = new FlatBlock[chunks.Length * 2][];
            Array.Copy(chunks, grown, chunks.Length);
            Volatile.Write(ref _chunks, grown);
            chunks = grown;
        }

        FlatBlock[]? chunk = chunks[chunkIndex];
        if (chunk is null)
        {
            chunk = new FlatBlock[ChunkSize];
            chunks[chunkIndex] = chunk;
        }

        chunk[index & ChunkMask] = block;

        // Volatile: publishes the element store to readers.
        _count = index + 1;
    }
}
