using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using AvaloniaMarkdown.Ast;
using AvaloniaMarkdown.Flattening;

namespace AvaloniaMarkdown.Rendering.Views;

/// <summary>
/// Renders an image block: lazy, cancellable, cached loading with an inline placeholder.
/// </summary>
/// <remarks>
/// <para>
/// The bitmap is requested only once the view is realised (i.e. scrolled into the viewport) and
/// the request is cancelled if it scrolls away again before completing. Decoding happens on a
/// background thread inside <see cref="Images.MarkdownImageCache"/>; the UI thread only assigns
/// the finished bitmap.
/// </para>
/// <para>
/// The block reserves a placeholder height until the bitmap arrives, so streaming never causes a
/// layout jump larger than one block.
/// </para>
/// </remarks>
public sealed class ImageBlockView : MarkdownBlockView
{
    private const double PlaceholderHeight = 120;

    private CancellationTokenSource? _cancellation;
    private Bitmap? _bitmap;
    private TextLayout? _captionLayout;
    private string? _requestedUrl;
    private bool _failed;
    private double _availableWidth;

    protected override void OnBlockChanged(FlatBlock? previous)
    {
        if (previous is not null && previous.ImageUrl == Block.ImageUrl)
        {
            return;
        }

        CancelPending();
        _bitmap = null;
        _failed = false;
        _requestedUrl = null;
        _captionLayout = null;
    }

    protected override void OnDetached()
    {
        CancelPending();
        _bitmap = null;
        _captionLayout = null;
        _requestedUrl = null;
        _failed = false;
    }

    private void CancelPending()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }

    protected override Size MeasureContent(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) && availableSize.Width > 0 ? availableSize.Width : 400;
        _availableWidth = width;

        StartLoad(width);

        if (_bitmap is null)
        {
            return new Size(width, _failed ? Theme.FontSize * 2.4 : PlaceholderHeight);
        }

        (double drawWidth, double drawHeight) = ScaledSize(width);
        return new Size(width, drawHeight + CaptionHeight());
    }

    private double CaptionHeight()
    {
        if (string.IsNullOrEmpty(Block.ImageTitle))
        {
            return 0;
        }

        _captionLayout ??= new TextLayout(
            Block.ImageTitle,
            Theme.GetTypeface(bold: false, italic: true, monospace: false),
            Theme.FontSize * 0.9,
            Theme.MutedForeground);

        return _captionLayout.Height + 4;
    }

    private (double Width, double Height) ScaledSize(double availableWidth)
    {
        if (_bitmap is null)
        {
            return (availableWidth, PlaceholderHeight);
        }

        double sourceWidth = _bitmap.Size.Width;
        double sourceHeight = _bitmap.Size.Height;
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return (availableWidth, PlaceholderHeight);
        }

        double scale = Math.Min(1, availableWidth / sourceWidth);
        double height = sourceHeight * scale;

        if (height > Theme.ImageMaxHeight)
        {
            scale *= Theme.ImageMaxHeight / height;
            height = Theme.ImageMaxHeight;
        }

        return (sourceWidth * scale, height);
    }

    private void StartLoad(double width)
    {
        string? url = Block.ImageUrl;
        if (string.IsNullOrEmpty(url) || _requestedUrl == url || _failed)
        {
            return;
        }

        _requestedUrl = url;
        _cancellation = new CancellationTokenSource();
        CancellationToken token = _cancellation.Token;
        int decodeWidth = (int)Math.Ceiling(Math.Max(64, width));
        IMarkdownHost host = Host;

        _ = LoadAsync(host, url, decodeWidth, token);
    }

    private async Task LoadAsync(IMarkdownHost host, string url, int decodeWidth, CancellationToken token)
    {
        try
        {
            Bitmap? bitmap = await host.LoadImageAsync(url, decodeWidth, token).ConfigureAwait(true);
            if (token.IsCancellationRequested || Block.ImageUrl != url)
            {
                return;
            }

            _bitmap = bitmap;
            _failed = bitmap is null;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            _failed = true;
        }

        host.InvalidateBlockMeasure(this);
    }

    protected override void RenderContent(DrawingContext context, Rect contentRect)
    {
        MarkdownTheme theme = Theme;

        if (_bitmap is null)
        {
            RenderPlaceholder(context, contentRect);
            return;
        }

        (double width, double height) = ScaledSize(contentRect.Width);
        var destination = new Rect(contentRect.X, contentRect.Y, width, height);
        context.DrawImage(_bitmap, new Rect(_bitmap.Size), destination);

        if (_captionLayout is not null)
        {
            _captionLayout.Draw(context, new Point(contentRect.X, destination.Bottom + 4));
        }
    }

    private void RenderPlaceholder(DrawingContext context, Rect contentRect)
    {
        MarkdownTheme theme = Theme;

        if (_failed)
        {
            var failedLayout = new TextLayout(
                $"\u26a0 {Block.ImageAlt ?? "image"}",
                theme.GetTypeface(bold: false, italic: true, monospace: false),
                theme.FontSize,
                theme.MutedForeground);

            failedLayout.Draw(context, contentRect.TopLeft);
            return;
        }

        var box = new Rect(contentRect.X, contentRect.Y, Math.Min(contentRect.Width, 320), PlaceholderHeight - 8);
        context.DrawRectangle(theme.CodeBackground, new Pen(theme.CodeBorder, 1, DashStyle.Dash), box, 6, 6);

        var layout = new TextLayout(
            string.IsNullOrEmpty(Block.ImageAlt) ? "Loading image\u2026" : Block.ImageAlt,
            theme.GetTypeface(bold: false, italic: false, monospace: false),
            theme.FontSize,
            theme.MutedForeground,
            TextAlignment.Center,
            TextWrapping.Wrap,
            maxWidth: box.Width - 16);

        layout.Draw(context, new Point(box.X + 8, box.Y + ((box.Height - layout.Height) / 2)));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (e.InitialPressMouseButton == MouseButton.Left && Block.ImageUrl is { Length: > 0 } url)
        {
            Host.OnTargetActivated(new InlineTarget(url, Block.ImageTitle, isImage: true));
            e.Handled = true;
        }
    }
}
