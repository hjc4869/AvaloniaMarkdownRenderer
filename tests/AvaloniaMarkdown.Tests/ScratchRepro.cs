using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using AvaloniaMarkdown.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace AvaloniaMarkdown.Tests;

public class ScratchRepro
{
    private readonly ITestOutputHelper _output;

    public ScratchRepro(ITestOutputHelper output) => _output = output;

    [AvaloniaFact]
    public void ThemeBackgroundIsPainted()
    {
        MarkdownDocument document = TestDocument.Create();
        document.Append("# Title\n\nBody text.\n");
        document.Complete();

        var view = new MarkdownView
        {
            Document = document,
            MarkdownTheme = MarkdownTheme.Dark,
            MinimumUpdateInterval = TimeSpan.Zero,
        };
        var window = new Window { Width = 400, Height = 300, Content = view };

        window.Show();
        Pump(window);

        _output.WriteLine($"dark  -> {Sample(window)}");

        view.MarkdownTheme = MarkdownTheme.Light;
        Pump(window);

        _output.WriteLine($"light -> {Sample(window)}");

        window.Close();
    }

    private static string Sample(Window window)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        if (frame is null)
        {
            return "no frame";
        }

        using ILockedFramebuffer buffer = frame.Lock();
        nint address = buffer.Address + (buffer.RowBytes * 200) + (4 * 200);
        int bgra = Marshal.ReadInt32(address);

        return $"B={bgra & 0xFF} G={(bgra >> 8) & 0xFF} R={(bgra >> 16) & 0xFF} A={(bgra >> 24) & 0xFF} fmt={buffer.Format}";
    }

    private static void Pump(Window window)
    {
        for (int i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
