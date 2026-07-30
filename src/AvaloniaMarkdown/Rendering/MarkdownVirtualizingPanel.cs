using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using AvaloniaMarkdown.Diffing;
using AvaloniaMarkdown.Flattening;
using AvaloniaMarkdown.Rendering.Views;

namespace AvaloniaMarkdown.Rendering;

/// <summary>
/// Viewport-virtualising panel that materialises only the blocks the user can see.
/// </summary>
/// <remarks>
/// <para>
/// The panel implements <see cref="ILogicalScrollable"/>, so the enclosing
/// <see cref="ScrollViewer"/> delegates scrolling to it instead of laying out the whole document.
/// Block extents live in a Fenwick tree (<see cref="BlockHeightCache"/>) which answers
/// "which block is at pixel y" in O(log n), so a document with 100 000 blocks scrolls at the same
/// cost as one with ten.
/// </para>
/// <para>
/// Realised controls are recycled through per-kind pools. Appending a streamed token therefore
/// touches at most one control, and the visual tree is never rebuilt.
/// </para>
/// </remarks>
public class MarkdownVirtualizingPanel : Panel, ILogicalScrollable
{
    private readonly BlockHeightCache _heights = new();
    private readonly Dictionary<int, MarkdownBlockView> _realized = new();
    private readonly Dictionary<FlatBlockKind, Stack<MarkdownBlockView>> _pool = new();
    private readonly List<int> _scratch = new();

    private MarkdownSnapshot _snapshot = MarkdownSnapshot.Empty;
    private IMarkdownHost? _host;
    private Size _viewport;
    private Size _extent;
    private Vector _offset;
    private int _firstRealized;
    private int _afterLastRealized;

    /// <summary>Extra pixels realised above and below the viewport.</summary>
    public double OverscanPixels { get; set; } = 240;

    /// <summary>Number of controls currently materialised. Exposed for tests and diagnostics.</summary>
    public int RealizedCount => _realized.Count;

    /// <summary>Index of the first realised block.</summary>
    public int FirstRealizedIndex => _firstRealized;

    /// <summary>Index just past the last realised block.</summary>
    public int LastRealizedIndexExclusive => _afterLastRealized;

    public MarkdownSnapshot Snapshot => _snapshot;

    internal void SetHost(IMarkdownHost host) => _host = host;

    /// <summary>True when the viewport is at (or within a pixel of) the bottom of the document.</summary>
    public bool IsScrolledToEnd => _extent.Height - (_offset.Y + _viewport.Height) < 2;

    // ------------------------------------------------------------------
    // Snapshot application
    // ------------------------------------------------------------------

    /// <summary>
    /// Applies a new snapshot, using <paramref name="operations"/> to avoid touching anything that
    /// did not change.
    /// </summary>
    public void ApplySnapshot(MarkdownSnapshot snapshot, IReadOnlyList<RenderOperation>? operations)
    {
        MarkdownSnapshot previous = _snapshot;
        _snapshot = snapshot;

        bool structural = operations is null || !previous.SharesPrefixWith(snapshot);

        if (!structural)
        {
            foreach (RenderOperation operation in operations!)
            {
                switch (operation.Kind)
                {
                    case RenderOperationKind.AppendBlock:
                        if (operation.Index == _heights.Count)
                        {
                            _heights.Append(operation.Block!.Kind);
                        }
                        else
                        {
                            structural = true;
                        }

                        break;

                    case RenderOperationKind.ReplaceBlock:
                    case RenderOperationKind.UpdateInline:
                        ApplyUpdate(operation.Index, operation.Block!);
                        break;

                    case RenderOperationKind.FinalizeBlock:
                        break;

                    default:
                        structural = true;
                        break;
                }

                if (structural)
                {
                    break;
                }
            }
        }

        if (structural)
        {
            RecycleAll();
            _heights.Clear();
            _heights.Resize(snapshot.Count, i => snapshot[i].Kind);
        }
        else if (_heights.Count != snapshot.Count)
        {
            _heights.Resize(snapshot.Count, i => snapshot[i].Kind);
        }

        InvalidateMeasure();
        RaiseScrollInvalidated(EventArgs.Empty);
    }

    private void ApplyUpdate(int index, FlatBlock block)
    {
        _heights.Invalidate(index);

        if (!_realized.TryGetValue(index, out MarkdownBlockView? view))
        {
            return;
        }

        if (IsCompatible(view, block.Kind))
        {
            view.UpdateBlock(block);
        }
        else
        {
            Recycle(index, view);
        }
    }

    /// <summary>Redraws every realised view belonging to <paramref name="blockId"/>.</summary>
    internal void InvalidateBlockVisual(int blockId)
    {
        foreach (MarkdownBlockView view in _realized.Values)
        {
            if (view.Block.BlockId == blockId)
            {
                view.InvalidateVisual();
            }
        }
    }

    /// <summary>Forces a full re-measure, e.g. after the theme changed.</summary>
    public void Rebuild()
    {
        RecycleAll();
        _heights.Clear();
        _heights.Resize(_snapshot.Count, i => _snapshot[i].Kind);
        InvalidateMeasure();
    }

    // ------------------------------------------------------------------
    // Layout
    // ------------------------------------------------------------------

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        double height = double.IsFinite(availableSize.Height) ? availableSize.Height : 0;

        _viewport = new Size(width, height);

        if (_snapshot.Count == 0 || _host is null || width <= 0)
        {
            RecycleAll();
            _extent = new Size(width, 0);
            return new Size(width, height);
        }

        RealizeViewport(width, height);

        _extent = new Size(width, _heights.TotalHeight);
        ClampOffset();

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach ((int index, MarkdownBlockView view) in _realized)
        {
            double top = _heights.PrefixSum(index) - _offset.Y;
            view.Arrange(new Rect(0, top, finalSize.Width, _heights.GetHeight(index)));
        }

        return finalSize;
    }

    private void RealizeViewport(double width, double height)
    {
        double top = Math.Max(0, _offset.Y - OverscanPixels);
        double bottom = _offset.Y + height + OverscanPixels;

        int first = _heights.FindIndex(top);
        double y = _heights.PrefixSum(first);

        int index = first;
        while (index < _snapshot.Count && (y < bottom || index == first))
        {
            MarkdownBlockView view = Realize(index);
            view.Measure(new Size(width, double.PositiveInfinity));

            double measured = view.DesiredSize.Height;
            if (!_heights.IsMeasured(index) || Math.Abs(measured - _heights.GetHeight(index)) > 0.05)
            {
                _heights.SetMeasured(index, _snapshot[index].Kind, measured);
            }

            y += measured;
            index++;
        }

        _firstRealized = first;
        _afterLastRealized = index;
        RecycleOutside(first, index);
    }

    // ------------------------------------------------------------------
    // Realisation and recycling
    // ------------------------------------------------------------------

    private MarkdownBlockView Realize(int index)
    {
        FlatBlock block = _snapshot[index];

        if (_realized.TryGetValue(index, out MarkdownBlockView? existing))
        {
            if (IsCompatible(existing, block.Kind))
            {
                if (!ReferenceEquals(existing.Block, block))
                {
                    existing.UpdateBlock(block);
                }

                return existing;
            }

            Recycle(index, existing);
        }

        MarkdownBlockView view = Rent(block.Kind);
        view.Attach(block, _host!);
        _realized[index] = view;

        if (!Children.Contains(view))
        {
            Children.Add(view);
        }

        return view;
    }

    private void RecycleOutside(int from, int toExclusive)
    {
        _scratch.Clear();
        foreach (int index in _realized.Keys)
        {
            if (index < from || index >= toExclusive || index >= _snapshot.Count)
            {
                _scratch.Add(index);
            }
        }

        foreach (int index in _scratch)
        {
            Recycle(index, _realized[index]);
        }

        _scratch.Clear();
    }

    private void RecycleAll()
    {
        _scratch.Clear();
        _scratch.AddRange(_realized.Keys);
        foreach (int index in _scratch)
        {
            Recycle(index, _realized[index]);
        }

        _scratch.Clear();
    }

    private void Recycle(int index, MarkdownBlockView view)
    {
        _realized.Remove(index);
        Children.Remove(view);
        view.Detach();

        FlatBlockKind kind = ViewKind(view);
        if (!_pool.TryGetValue(kind, out Stack<MarkdownBlockView>? stack))
        {
            stack = new Stack<MarkdownBlockView>();
            _pool[kind] = stack;
        }

        if (stack.Count < 128)
        {
            stack.Push(view);
        }
    }

    private MarkdownBlockView Rent(FlatBlockKind kind)
    {
        FlatBlockKind poolKind = PoolKind(kind);
        if (_pool.TryGetValue(poolKind, out Stack<MarkdownBlockView>? stack) && stack.Count > 0)
        {
            return stack.Pop();
        }

        return kind switch
        {
            FlatBlockKind.Code => new CodeSegmentView(),
            FlatBlockKind.Table => new TableBlockView(),
            FlatBlockKind.ThematicBreak => new ThematicBreakView(),
            FlatBlockKind.Image => new ImageBlockView(),
            _ => new RichTextBlockView(),
        };
    }

    private static FlatBlockKind PoolKind(FlatBlockKind kind) => kind switch
    {
        FlatBlockKind.Heading or FlatBlockKind.Html => FlatBlockKind.Paragraph,
        _ => kind,
    };

    private static FlatBlockKind ViewKind(MarkdownBlockView view) => view switch
    {
        CodeSegmentView => FlatBlockKind.Code,
        TableBlockView => FlatBlockKind.Table,
        ThematicBreakView => FlatBlockKind.ThematicBreak,
        ImageBlockView => FlatBlockKind.Image,
        _ => FlatBlockKind.Paragraph,
    };

    private static bool IsCompatible(MarkdownBlockView view, FlatBlockKind kind) =>
        ViewKind(view) == PoolKind(kind);

    // ------------------------------------------------------------------
    // ILogicalScrollable
    // ------------------------------------------------------------------

    public Size Extent => _extent;

    public Size Viewport => _viewport;

    public Vector Offset
    {
        get => _offset;
        set
        {
            Vector clamped = ClampOffset(value);
            if (clamped == _offset)
            {
                return;
            }

            _offset = clamped;
            InvalidateMeasure();
        }
    }

    public bool CanHorizontallyScroll { get; set; }

    public bool CanVerticallyScroll { get; set; } = true;

    public bool IsLogicalScrollEnabled => true;

    public Size ScrollSize => new(1, 48);

    public Size PageScrollSize => new(_viewport.Width, Math.Max(1, _viewport.Height - 24));

    public event EventHandler? ScrollInvalidated;

    public bool BringIntoView(Control target, Rect targetRect) => false;

    public Control? GetControlInDirection(NavigationDirection direction, Control? from) => null;

    public void RaiseScrollInvalidated(EventArgs e) => ScrollInvalidated?.Invoke(this, e);

    /// <summary>Scrolls so the bottom of the document is visible.</summary>
    public void ScrollToEnd() => SetOffsetAndNotify(Math.Max(0, _extent.Height - _viewport.Height));

    /// <summary>Scrolls block <paramref name="index"/> to the top of the viewport.</summary>
    public void ScrollToBlock(int index)
    {
        index = Math.Clamp(index, 0, Math.Max(0, _snapshot.Count - 1));
        SetOffsetAndNotify(_heights.PrefixSum(index));
    }

    /// <summary>
    /// Moves the offset and tells the enclosing <see cref="ScrollViewer"/> about it; without the
    /// notification the presenter keeps its stale offset and pushes it back on the next scroll.
    /// </summary>
    private void SetOffsetAndNotify(double y)
    {
        Vector previous = _offset;
        Offset = new Vector(_offset.X, y);

        if (_offset != previous)
        {
            RaiseScrollInvalidated(EventArgs.Empty);
        }
    }

    private void ClampOffset() => _offset = ClampOffset(_offset);

    private Vector ClampOffset(Vector value)
    {
        double maxY = Math.Max(0, _extent.Height - _viewport.Height);
        return new Vector(0, Math.Clamp(value.Y, 0, maxY));
    }
}
