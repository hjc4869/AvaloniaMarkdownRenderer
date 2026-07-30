using AvaloniaMarkdown.Flattening;

namespace AvaloniaMarkdown.Rendering;

/// <summary>
/// Tracks the vertical extent of every block, measured or estimated.
/// </summary>
/// <remarks>
/// <para>
/// Virtualisation needs two O(log n) primitives: "what is the pixel offset of block i" and "which
/// block covers pixel y". A Fenwick (binary indexed) tree over per-block heights provides both,
/// so a 100 000 block document computes its scroll extent and viewport range in microseconds.
/// </para>
/// <para>
/// Blocks that have never been realised contribute a per-kind running estimate that is frozen at
/// insertion time. Freezing it means improving the estimate later cannot retroactively move
/// content the user is already looking at.
/// </para>
/// </remarks>
internal sealed class BlockHeightCache
{
    private const double InitialEstimate = 26;

    private readonly double[] _kindEstimates;
    private readonly int[] _kindSamples;

    private double[] _heights = new double[1024];
    private double[] _tree = new double[1024];
    private bool[] _measured = new bool[1024];
    private int _count;

    public BlockHeightCache()
    {
        int kinds = Enum.GetValues<FlatBlockKind>().Length;
        _kindEstimates = new double[kinds];
        _kindSamples = new int[kinds];
        Array.Fill(_kindEstimates, InitialEstimate);
    }

    public int Count => _count;

    public double TotalHeight => PrefixSum(_count);

    public bool IsMeasured(int index) => (uint)index < (uint)_count && _measured[index + 1];

    public double GetHeight(int index) => (uint)index < (uint)_count ? _heights[index + 1] : 0;

    public void Clear()
    {
        _count = 0;
        Array.Clear(_tree);
        Array.Clear(_measured);
    }

    /// <summary>Appends a block with an estimated height. O(log n).</summary>
    public void Append(FlatBlockKind kind)
    {
        EnsureCapacity(_count + 2);

        int i = ++_count;
        double value = _kindEstimates[(int)kind];
        _heights[i] = value;
        _measured[i] = false;

        _tree[i] = value;
        int lowBit = i & -i;
        for (int step = 1; step < lowBit; step <<= 1)
        {
            _tree[i] += _tree[i - step];
        }
    }

    /// <summary>Records a measured height. O(log n).</summary>
    public void SetMeasured(int index, FlatBlockKind kind, double height)
    {
        if ((uint)index >= (uint)_count)
        {
            return;
        }

        int i = index + 1;
        double delta = height - _heights[i];
        _heights[i] = height;

        if (!_measured[i])
        {
            _measured[i] = true;
            int kindIndex = (int)kind;
            int samples = Math.Min(256, _kindSamples[kindIndex] + 1);
            _kindSamples[kindIndex] = samples;
            _kindEstimates[kindIndex] += (height - _kindEstimates[kindIndex]) / samples;
        }

        if (delta == 0)
        {
            return;
        }

        for (; i <= _count; i += i & -i)
        {
            _tree[i] += delta;
        }
    }

    /// <summary>Marks a block as needing re-measurement without changing its current height.</summary>
    public void Invalidate(int index)
    {
        if ((uint)index < (uint)_count)
        {
            _measured[index + 1] = false;
        }
    }

    /// <summary>Truncates or grows the cache to <paramref name="newCount"/> entries.</summary>
    public void Resize(int newCount, Func<int, FlatBlockKind> kindLookup)
    {
        if (newCount == _count)
        {
            return;
        }

        if (newCount > _count)
        {
            for (int i = _count; i < newCount; i++)
            {
                Append(kindLookup(i));
            }

            return;
        }

        _count = newCount;
        Rebuild();
    }

    /// <summary>Sum of the heights of blocks [0, count). O(log n).</summary>
    public double PrefixSum(int count)
    {
        double sum = 0;
        for (int i = Math.Min(count, _count); i > 0; i -= i & -i)
        {
            sum += _tree[i];
        }

        return sum;
    }

    /// <summary>Index of the block containing <paramref name="offset"/>. O(log n).</summary>
    public int FindIndex(double offset)
    {
        if (offset <= 0 || _count == 0)
        {
            return 0;
        }

        int position = 0;
        int bit = HighestPowerOfTwo(_count);

        for (; bit > 0; bit >>= 1)
        {
            int next = position + bit;
            if (next <= _count && _tree[next] <= offset)
            {
                position = next;
                offset -= _tree[next];
            }
        }

        return Math.Min(position, _count - 1);
    }

    private void Rebuild()
    {
        Array.Clear(_tree, 0, _count + 1);
        for (int i = 1; i <= _count; i++)
        {
            _tree[i] += _heights[i];
            int parent = i + (i & -i);
            if (parent <= _count)
            {
                _tree[parent] += _tree[i];
            }
        }
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _heights.Length)
        {
            return;
        }

        int capacity = _heights.Length;
        while (capacity < required)
        {
            capacity *= 2;
        }

        Array.Resize(ref _heights, capacity);
        Array.Resize(ref _measured, capacity);
        Array.Resize(ref _tree, capacity);
    }

    private static int HighestPowerOfTwo(int value)
    {
        int result = 1;
        while (result << 1 <= value)
        {
            result <<= 1;
        }

        return result;
    }
}
