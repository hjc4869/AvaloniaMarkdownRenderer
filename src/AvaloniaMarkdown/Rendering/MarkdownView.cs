using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaMarkdown.Ast;
using AvaloniaMarkdown.Diffing;
using AvaloniaMarkdown.Flattening;
using AvaloniaMarkdown.Images;

namespace AvaloniaMarkdown.Rendering;

/// <summary>Raised when the user activates a link or an image.</summary>
public sealed class MarkdownLinkEventArgs : EventArgs
{
    internal MarkdownLinkEventArgs(InlineTarget target)
    {
        Target = target;
    }

    public InlineTarget Target { get; }

    public string Url => Target.Url;

    /// <summary>Set to true to suppress the default "open in browser" behaviour.</summary>
    public bool Handled { get; set; }
}

/// <summary>
/// The markdown rendering control.
/// </summary>
/// <remarks>
/// <para>
/// Bind a <see cref="MarkdownDocument"/> and append to it from anywhere; the view listens for
/// snapshots on the parser thread, coalesces them to at most one visual update per frame, and
/// applies them as a minimal set of <see cref="RenderOperation"/>s.
/// </para>
/// <example>
/// <code>
/// var document = new MarkdownDocument();
/// markdownView.Bind(document);
///
/// await foreach (var chunk in stream)
/// {
///     document.Append(chunk);   // any thread
/// }
///
/// document.Complete();
/// </code>
/// </example>
/// </remarks>
public class MarkdownView : Decorator, IMarkdownHost
{
    public static readonly StyledProperty<MarkdownDocument?> DocumentProperty =
        AvaloniaProperty.Register<MarkdownView, MarkdownDocument?>(nameof(Document));

    public static readonly StyledProperty<MarkdownTheme> MarkdownThemeProperty =
        AvaloniaProperty.Register<MarkdownView, MarkdownTheme>(nameof(MarkdownTheme), MarkdownTheme.Light);

    public static readonly StyledProperty<bool> AutoScrollToEndProperty =
        AvaloniaProperty.Register<MarkdownView, bool>(nameof(AutoScrollToEnd), true);

    public static readonly StyledProperty<bool> DeferUpdatesWhileScrollingProperty =
        AvaloniaProperty.Register<MarkdownView, bool>(nameof(DeferUpdatesWhileScrolling), true);

    private readonly MarkdownVirtualizingPanel _panel = new();
    private readonly ScrollViewer _scrollViewer;
    private readonly BlockDiffEngine _diff = new();
    private readonly DispatcherTimer _flushTimer;

    private TimeSpan _minimumUpdateInterval = TimeSpan.FromMilliseconds(16);
    private MarkdownSnapshot _applied = MarkdownSnapshot.Empty;
    private MarkdownSnapshot? _pending;
    private MarkdownDocument? _subscribed;
    private long _lastScrollTicks;
    private long _lastFlushTicks;
    private long _pendingSinceTicks;
    private int _wakeupPosted;
    private double _programmaticOffsetY = double.NaN;
    private bool _pinnedToEnd = true;

    public MarkdownView()
    {
        _panel.SetHost(this);

        _scrollViewer = new ScrollViewer
        {
            Content = _panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        _scrollViewer.ScrollChanged += OnScrollChanged;
        AddHandler(
            Avalonia.Input.InputElement.PointerWheelChangedEvent,
            (object? _, Avalonia.Input.PointerWheelEventArgs _) => _lastScrollTicks = Environment.TickCount64,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

        Child = _scrollViewer;

        _flushTimer = new DispatcherTimer(_minimumUpdateInterval, DispatcherPriority.Render, OnFlushTick);
    }

    /// <summary>Raised on the UI thread when a link or image is activated.</summary>
    public event EventHandler<MarkdownLinkEventArgs>? LinkClicked;

    /// <summary>The document to render.</summary>
    public MarkdownDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>Colours, fonts and metrics.</summary>
    public MarkdownTheme MarkdownTheme
    {
        get => GetValue(MarkdownThemeProperty);
        set => SetValue(MarkdownThemeProperty, value);
    }

    /// <summary>Keep the viewport pinned to the bottom while streaming, like a chat transcript.</summary>
    public bool AutoScrollToEnd
    {
        get => GetValue(AutoScrollToEndProperty);
        set => SetValue(AutoScrollToEndProperty, value);
    }

    /// <summary>Hold back visual updates while the user is actively scrolling.</summary>
    public bool DeferUpdatesWhileScrolling
    {
        get => GetValue(DeferUpdatesWhileScrollingProperty);
        set => SetValue(DeferUpdatesWhileScrollingProperty, value);
    }

    /// <summary>
    /// Shortest wall-clock gap between two visual updates. Snapshots that arrive inside the
    /// interval are buffered and applied together on the next tick, so a burst of streamed tokens
    /// costs one diff, one measure pass and one set of Avalonia calls instead of one per token.
    /// Defaults to 16 ms (one frame at 60 Hz). Set to <see cref="TimeSpan.Zero"/> to apply every
    /// snapshot as soon as it arrives.
    /// </summary>
    public TimeSpan MinimumUpdateInterval
    {
        get => _minimumUpdateInterval;
        set
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The interval cannot be negative.");
            }

            _minimumUpdateInterval = value;
            _flushTimer.Interval = value > TimeSpan.Zero ? value : TimeSpan.FromMilliseconds(1);
        }
    }

    /// <summary>How long after the last scroll event updates stay deferred.</summary>
    public TimeSpan ScrollQuietPeriod { get; set; } = TimeSpan.FromMilliseconds(120);

    /// <summary>Upper bound on how long an update may be deferred.</summary>
    public TimeSpan MaximumDeferral { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Image loader used by image blocks.</summary>
    public MarkdownImageCache ImageCache { get; set; } = MarkdownImageCache.Shared;

    /// <summary>The virtualising panel; exposed for diagnostics and tests.</summary>
    public MarkdownVirtualizingPanel Panel => _panel;

    public ScrollViewer ScrollViewer => _scrollViewer;

    /// <summary>Attaches <paramref name="document"/> to this view.</summary>
    public void Bind(MarkdownDocument document)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
    }

    /// <summary>Scrolls to the end of the document.</summary>
    public void ScrollToEnd() => ScrollToEndInternal();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DocumentProperty)
        {
            Detach();
            Attach(change.GetNewValue<MarkdownDocument?>());
        }
        else if (change.Property == MarkdownThemeProperty)
        {
            _panel.Rebuild();
            InvalidateVisual();
        }
    }

    /// <summary>Paints the theme page background behind the document.</summary>
    public override void Render(Avalonia.Media.DrawingContext context)
    {
        Avalonia.Media.IBrush? background = MarkdownTheme.Background;
        if (background is not null)
        {
            context.FillRectangle(background, new Rect(Bounds.Size));
        }

        base.Render(context);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _flushTimer.Stop();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (_pending is not null)
        {
            _flushTimer.Start();
        }
    }

    // ------------------------------------------------------------------
    // Document plumbing
    // ------------------------------------------------------------------

    private void Attach(MarkdownDocument? document)
    {
        _subscribed = document;
        _applied = MarkdownSnapshot.Empty;
        _lastFlushTicks = 0;

        if (document is null)
        {
            _panel.ApplySnapshot(MarkdownSnapshot.Empty, null);
            return;
        }

        document.SnapshotChanged += OnSnapshotChanged;
        Publish(document.Snapshot);
    }

    private void Detach()
    {
        if (_subscribed is not null)
        {
            _subscribed.SnapshotChanged -= OnSnapshotChanged;
            _subscribed = null;
        }

        _pending = null;
        _flushTimer.Stop();
    }

    /// <summary>Called on the parser thread; only stores the snapshot and wakes the UI timer.</summary>
    private void OnSnapshotChanged(object? sender, MarkdownSnapshot snapshot) => Publish(snapshot);

    private void Publish(MarkdownSnapshot snapshot)
    {
        if (Interlocked.Exchange(ref _pending, snapshot) is null)
        {
            Interlocked.Exchange(ref _pendingSinceTicks, Environment.TickCount64);
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            if (!_flushTimer.IsEnabled)
            {
                _flushTimer.Start();
            }

            Flush();
        }
        else if (Interlocked.CompareExchange(ref _wakeupPosted, 1, 0) == 0)
        {
            // A single wakeup is enough: the snapshot is already visible in _pending and the flush
            // timer keeps ticking. Posting per snapshot would let a fast producer fill the
            // dispatcher queue with render-priority work items and starve input handling.
            Dispatcher.UIThread.Post(static state => ((MarkdownView)state!).StartTimer(), this, DispatcherPriority.Render);
        }
    }

    private void StartTimer()
    {
        Volatile.Write(ref _wakeupPosted, 0);

        if (!_flushTimer.IsEnabled)
        {
            _flushTimer.Start();
        }

        Flush();
    }

    private void OnFlushTick(object? sender, EventArgs e) => Flush();

    /// <summary>
    /// Only genuine user scrolling defers updates. Growth of the document and auto-scrolling also
    /// raise <see cref="ScrollViewer.ScrollChanged"/> (asynchronously, after layout), and treating
    /// those as user activity would stall streaming updates indefinitely.
    /// </summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta.Y == 0)
        {
            return;
        }

        // The notification for a programmatic scroll arrives after ScrollToEndInternal has
        // returned, so the offset itself is what identifies it.
        if (!double.IsNaN(_programmaticOffsetY) && Math.Abs(_panel.Offset.Y - _programmaticOffsetY) < 0.5)
        {
            _programmaticOffsetY = double.NaN;
            return;
        }

        _lastScrollTicks = Environment.TickCount64;
        _pinnedToEnd = _panel.IsScrolledToEnd;
    }

    private void Flush()
    {
        MarkdownSnapshot? snapshot = Volatile.Read(ref _pending);
        if (snapshot is null)
        {
            _flushTimer.Stop();
            return;
        }

        if (snapshot.Version == _applied.Version && snapshot.Generation == _applied.Generation)
        {
            Interlocked.CompareExchange(ref _pending, null, snapshot);
            _flushTimer.Stop();
            return;
        }

        long now = Environment.TickCount64;
        if (DeferUpdatesWhileScrolling &&
            now - _lastScrollTicks < ScrollQuietPeriod.TotalMilliseconds &&
            now - Interlocked.Read(ref _pendingSinceTicks) < MaximumDeferral.TotalMilliseconds)
        {
            return;
        }

        // Buffer everything that arrives inside the minimum interval; the timer is running, so the
        // accumulated snapshot is applied as a single batch on the next tick.
        if (_lastFlushTicks != 0 && now - _lastFlushTicks < _minimumUpdateInterval.TotalMilliseconds)
        {
            return;
        }

        _lastFlushTicks = now;
        Interlocked.CompareExchange(ref _pending, null, snapshot);

        bool wasAtEnd = _pinnedToEnd || _panel.IsScrolledToEnd;

        IReadOnlyList<RenderOperation> operations = _diff.Diff(_applied, snapshot);
        _applied = snapshot;
        _panel.ApplySnapshot(snapshot, operations);

        if (AutoScrollToEnd && wasAtEnd)
        {
            _pinnedToEnd = true;
            Dispatcher.UIThread.Post(ScrollToEndInternal, DispatcherPriority.Loaded);
        }
    }

    private void ScrollToEndInternal() => ScrollToEndInternal(retry: true);

    /// <summary>
    /// Scrolls to the bottom. The extent only becomes exact once the appended blocks have been
    /// measured, so a single retry after the next layout pass lands on the real end.
    /// </summary>
    private void ScrollToEndInternal(bool retry)
    {
        _panel.ScrollToEnd();
        _programmaticOffsetY = _panel.Offset.Y;

        if (retry)
        {
            Dispatcher.UIThread.Post(static state => ((MarkdownView)state!).ScrollToEndInternal(false), this, DispatcherPriority.Loaded);
        }
    }

    // ------------------------------------------------------------------
    // IMarkdownHost
    // ------------------------------------------------------------------

    MarkdownTheme IMarkdownHost.Theme => MarkdownTheme;

    void IMarkdownHost.OnTargetActivated(InlineTarget target)
    {
        var args = new MarkdownLinkEventArgs(target);
        LinkClicked?.Invoke(this, args);

        if (args.Handled)
        {
            return;
        }

        if (!Uri.TryCreate(target.Url, UriKind.Absolute, out Uri? uri))
        {
            return;
        }

        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        _ = topLevel?.Launcher.LaunchUriAsync(uri);
    }

    string IMarkdownHost.GetCodeBlockText(int blockId)
    {
        var builder = new System.Text.StringBuilder();
        MarkdownSnapshot snapshot = _applied;

        for (int i = 0; i < snapshot.Count; i++)
        {
            FlatBlock block = snapshot[i];
            if (block.BlockId != blockId || block.Kind != FlatBlockKind.Code)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(block.CodeText);
        }

        return builder.ToString();
    }

    void IMarkdownHost.CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        _ = clipboard?.SetTextAsync(text);
    }

    Task<Bitmap?> IMarkdownHost.LoadImageAsync(string url, int decodeWidth, CancellationToken cancellationToken) =>
        ImageCache.GetAsync(url, decodeWidth, cancellationToken);

    void IMarkdownHost.InvalidateBlockMeasure(MarkdownBlockView view)
    {
        view.InvalidateMeasure();
        view.InvalidateVisual();
        _panel.InvalidateMeasure();
    }

    void IMarkdownHost.InvalidateBlock(int blockId) => _panel.InvalidateBlockVisual(blockId);
}
