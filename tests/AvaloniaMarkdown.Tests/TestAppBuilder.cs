using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using AvaloniaMarkdown.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace AvaloniaMarkdown.Tests;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }
}

/// <summary>Bootstraps a headless Avalonia app (with real Skia) for the UI tests.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<HeadlessTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
