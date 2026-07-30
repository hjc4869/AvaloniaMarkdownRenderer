using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaMarkdown.Rendering;

namespace AvaloniaMarkdown.Demo;

/// <summary>
/// Demo shell: streams a large sample document token by token and reports live statistics.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly MarkdownView _markdownView;
    private readonly TextBlock _stats;
    private readonly DispatcherTimer _statsTimer;
    private readonly Button _streamButton;
    private readonly Slider _speedSlider;

    private MarkdownDocument _document = new();
    private CancellationTokenSource? _streamCancellation;
    private long _appendCount;
    private double _appendMicrosecondsTotal;
    private double _appendMicrosecondsMax;

    /// <summary>
    /// Mirror of the slider, updated on the UI thread. Avalonia controls may only be touched from
    /// the UI thread, and the streaming loop runs on the thread pool.
    /// </summary>
    private volatile float _tokensPerSecond = 60;

    private string? _streamError;

    public MainWindow()
    {
        Title = "Avalonia Streaming Markdown Renderer";
        Width = 1100;
        Height = 820;

        _markdownView = new MarkdownView
        {
            MarkdownTheme = MarkdownTheme.Dark,
            Document = _document,
            Margin = new Thickness(16, 8, 16, 8),
        };

        _markdownView.LinkClicked += (_, e) => Debug.WriteLine($"Link clicked: {e.Url}");

        _stats = new TextBlock
        {
            FontFamily = new FontFamily("monospace"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.Gray,
        };

        _streamButton = new Button { Content = "Stream sample" };
        _streamButton.Click += (_, _) => ToggleStream();

        var bigButton = new Button { Content = "Load 100k blocks" };
        bigButton.Click += (_, _) => LoadHugeDocument();

        var clearButton = new Button { Content = "Clear" };
        clearButton.Click += (_, _) => Reset();

        var themeButton = new Button { Content = "Toggle theme" };
        themeButton.Click += (_, _) => ApplyTheme(
            ReferenceEquals(_markdownView.MarkdownTheme, MarkdownTheme.Dark) ? MarkdownTheme.Light : MarkdownTheme.Dark);

        _speedSlider = new Slider { Minimum = 1, Maximum = 500, Value = 60, Width = 160 };
        _speedSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                _tokensPerSecond = (float)_speedSlider.Value;
            }
        };

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(16, 12, 16, 8),
            Children =
            {
                _streamButton,
                bigButton,
                clearButton,
                themeButton,
                new TextBlock { Text = "tokens/s", VerticalAlignment = VerticalAlignment.Center },
                _speedSlider,
                _stats,
            },
        };

        Content = new DockPanel
        {
            Children =
            {
                Dock(toolbar, Avalonia.Controls.Dock.Top),
                _markdownView,
            },
        };

        _statsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => UpdateStats());
        _statsTimer.Start();

        ApplyTheme(MarkdownTheme.Dark);

        _document.Append(SampleDocument.Text);
        _document.Complete();
    }

    /// <summary>Switches the markdown theme and matches the window chrome to it.</summary>
    private void ApplyTheme(MarkdownTheme theme)
    {
        _markdownView.MarkdownTheme = theme;
        Background = theme.Background;
        RequestedThemeVariant = ReferenceEquals(theme, MarkdownTheme.Dark)
            ? Avalonia.Styling.ThemeVariant.Dark
            : Avalonia.Styling.ThemeVariant.Light;
    }

    private static Control Dock(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private void Reset()
    {
        _streamCancellation?.Cancel();
        _appendCount = 0;
        _appendMicrosecondsTotal = 0;
        _appendMicrosecondsMax = 0;
        _streamError = null;

        _document = new MarkdownDocument();
        _markdownView.Document = _document;
    }

    private void ToggleStream()
    {
        if (_streamCancellation is not null)
        {
            _streamCancellation.Cancel();
            _streamCancellation = null;
            _streamButton.Content = "Stream sample";
            return;
        }

        Reset();
        _streamButton.Content = "Stop";

        var cancellation = new CancellationTokenSource();
        _streamCancellation = cancellation;
        _ = StreamAsync(SampleDocument.Text, cancellation.Token);
    }

    /// <summary>
    /// Simulates an LLM token stream on a background thread. The sample text is repeated
    /// indefinitely (separated by a horizontal rule) until the user stops the stream.
    /// </summary>
    private async Task StreamAsync(string text, CancellationToken cancellationToken)
    {
        const string RepeatSeparator = "\n\n---\n\n";

        MarkdownDocument document = _document;
        int index = 0;
        var stopwatch = new Stopwatch();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (index >= text.Length)
                {
                    document.Append(RepeatSeparator);
                    index = 0;
                }

                int length = Math.Min(Random.Shared.Next(2, 9), text.Length - index);
                string chunk = text.Substring(index, length);
                index += length;

                stopwatch.Restart();
                document.Append(chunk);
                stopwatch.Stop();

                double microseconds = stopwatch.Elapsed.TotalMilliseconds * 1000;
                _appendCount++;
                _appendMicrosecondsTotal += microseconds;
                _appendMicrosecondsMax = Math.Max(_appendMicrosecondsMax, microseconds);

                double tokensPerSecond = Math.Max(1, _tokensPerSecond);
                await Task.Delay(TimeSpan.FromSeconds(1 / tokensPerSecond), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _streamError = $"{ex.GetType().Name}: {ex.Message}";
            Debug.WriteLine($"Streaming failed: {ex}");

            document.Complete();
            _streamCancellation = null;
            await Dispatcher.UIThread.InvokeAsync(() => _streamButton.Content = "Stream sample");
        }
    }

    private void LoadHugeDocument()
    {
        Reset();

        var builder = new System.Text.StringBuilder(6 * 1024 * 1024);
        for (int i = 0; i < 20_000; i++)
        {
            builder.Append("## Section ").Append(i).Append("\n\n");
            builder.Append("Paragraph ").Append(i)
                   .Append(" with **bold**, *italic*, `code` and a [link](https://example.com/").Append(i).Append(").\n\n");
            builder.Append("- item one\n- item two\n- item three\n\n");
            if (i % 10 == 0)
            {
                builder.Append("```csharp\npublic void Method").Append(i).Append("()\n{\n    Console.WriteLine(\"")
                       .Append(i).Append("\");\n}\n```\n\n");
            }
        }

        _document.Append(builder.ToString());
        _document.Complete();
    }

    private void UpdateStats()
    {
        int total = _markdownView.Panel.Snapshot.Count;
        int realized = _markdownView.Panel.RealizedCount;
        double average = _appendCount == 0 ? 0 : _appendMicrosecondsTotal / _appendCount;

        _stats.Text =
            $"blocks {total,7:N0}   realized {realized,3}   " +
            $"append avg {average,6:F1} us   max {_appendMicrosecondsMax,7:F1} us   " +
            $"managed {GC.GetTotalMemory(false) / (1024 * 1024),4:N0} MB   gc0 {GC.CollectionCount(0)}" +
            (_streamError is null ? string.Empty : $"   ERROR {_streamError}");
    }
}
