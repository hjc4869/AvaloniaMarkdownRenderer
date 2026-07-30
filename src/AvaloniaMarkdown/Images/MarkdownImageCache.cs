using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AvaloniaMarkdown.Parsing;

namespace AvaloniaMarkdown.Images;

/// <summary>
/// Asynchronous, cancellable image loader with a bounded in-memory cache and an optional disk cache.
/// </summary>
/// <remarks>
/// <para>
/// Decoding happens entirely off the UI thread and is deduplicated per URL: ten blocks referencing
/// the same image trigger exactly one download and one decode. Bitmaps are downscaled during
/// decode, so a 4000px asset displayed at 600px never allocates the full-size surface.
/// </para>
/// <para>
/// Only schemes on <see cref="UriSafety"/>'s image allow list are fetched, responses are size and
/// time limited, and disk cache file names are content-addressed hashes so a hostile URL cannot
/// escape the cache directory.
/// </para>
/// </remarks>
public sealed class MarkdownImageCache : IDisposable
{
    private const int MaxDecodeWidth = 1600;
    private const long MaxResponseBytes = 32L * 1024 * 1024;

    private readonly ConcurrentDictionary<string, Lazy<Task<Bitmap?>>> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheEntry> _memory = new(StringComparer.Ordinal);
    private readonly HttpClient _http;
    private long _memoryBytes;
    private bool _disposed;

    public MarkdownImageCache(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
    }

    /// <summary>Process-wide default instance used by <see cref="Rendering.MarkdownView"/>.</summary>
    public static MarkdownImageCache Shared { get; } = new();

    /// <summary>Soft limit for decoded bitmap memory. Least recently used entries are evicted first.</summary>
    public long MaxMemoryBytes { get; set; } = 192L * 1024 * 1024;

    /// <summary>Enables an on-disk cache for remote images when set to an existing directory.</summary>
    public string? DiskCacheDirectory { get; set; }

    /// <summary>Base address used to resolve relative image URLs.</summary>
    public Uri? BaseUri { get; set; }

    /// <summary>Returns a decoded bitmap, or <c>null</c> when the image cannot be shown.</summary>
    public Task<Bitmap?> GetAsync(string url, int decodeWidth, CancellationToken cancellationToken)
    {
        if (!UriSafety.IsAllowedImageUrl(url))
        {
            return Task.FromResult<Bitmap?>(null);
        }

        int bucket = Bucket(decodeWidth);
        string key = string.Concat(url, "|", bucket.ToString());

        if (_memory.TryGetValue(key, out CacheEntry? cached))
        {
            cached.Touch();
            return Task.FromResult<Bitmap?>(cached.Bitmap);
        }

        Lazy<Task<Bitmap?>> lazy = _inFlight.GetOrAdd(
            key,
            static (k, state) => new Lazy<Task<Bitmap?>>(() => state.Self.LoadAsync(k, state.Url, state.Bucket)),
            (Self: this, Url: url, Bucket: bucket));

        return AwaitWithCancellation(lazy.Value, cancellationToken);
    }

    private static async Task<Bitmap?> AwaitWithCancellation(Task<Bitmap?> task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return await task.ConfigureAwait(false);
        }

        var cancellation = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(static state => ((TaskCompletionSource<Bitmap?>)state!).TrySetCanceled(), cancellation))
        {
            Task<Bitmap?> completed = await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false);
            return await completed.ConfigureAwait(false);
        }
    }

    private async Task<Bitmap?> LoadAsync(string key, string url, int decodeWidth)
    {
        try
        {
            byte[]? payload = await FetchAsync(url).ConfigureAwait(false);
            if (payload is null || payload.Length == 0)
            {
                return null;
            }

            Bitmap bitmap = Decode(payload, decodeWidth);
            Store(key, bitmap);
            return bitmap;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private static Bitmap Decode(byte[] payload, int decodeWidth)
    {
        using var stream = new MemoryStream(payload, writable: false);

        // Small assets decode fully; large ones are downscaled during decode to bound memory.
        if (payload.Length < 512 * 1024)
        {
            return new Bitmap(stream);
        }

        return Bitmap.DecodeToWidth(stream, Math.Clamp(decodeWidth, 64, MaxDecodeWidth), BitmapInterpolationMode.HighQuality);
    }

    private async Task<byte[]?> FetchAsync(string url)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = url.IndexOf(',');
            if (comma < 0 || url.IndexOf("base64", 0, comma, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return null;
            }

            return Convert.FromBase64String(url[(comma + 1)..]);
        }

        if (url.StartsWith("avares:", StringComparison.OrdinalIgnoreCase))
        {
            await using Stream asset = AssetLoader.Open(new Uri(url));
            return await ReadAllAsync(asset).ConfigureAwait(false);
        }

        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(url);
            return await File.ReadAllBytesAsync(uri.LocalPath).ConfigureAwait(false);
        }

        bool remote = url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                      url.StartsWith("https:", StringComparison.OrdinalIgnoreCase);

        if (!remote)
        {
            if (BaseUri is null)
            {
                return File.Exists(url) ? await File.ReadAllBytesAsync(url).ConfigureAwait(false) : null;
            }

            var resolved = new Uri(BaseUri, url);
            return await FetchAsync(resolved.ToString()).ConfigureAwait(false);
        }

        string? diskPath = GetDiskCachePath(url);
        if (diskPath is not null && File.Exists(diskPath))
        {
            return await File.ReadAllBytesAsync(diskPath).ConfigureAwait(false);
        }

        using HttpResponseMessage response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            return null;
        }

        await using Stream content = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        byte[] bytes = await ReadAllAsync(content).ConfigureAwait(false);

        if (diskPath is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
                await File.WriteAllBytesAsync(diskPath, bytes).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A failed disk cache write must never fail the render.
            }
        }

        return bytes;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        long total = 0;

        while ((read = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxResponseBytes)
            {
                throw new InvalidDataException("Image exceeds the maximum allowed size.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private string? GetDiskCachePath(string url)
    {
        if (DiskCacheDirectory is not { Length: > 0 } directory)
        {
            return null;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        string name = Convert.ToHexString(hash);
        return Path.Combine(directory, name[..2], name);
    }

    private void Store(string key, Bitmap bitmap)
    {
        long size = EstimateBytes(bitmap);
        var entry = new CacheEntry(bitmap, size);

        if (_memory.TryAdd(key, entry))
        {
            Interlocked.Add(ref _memoryBytes, size);
        }

        Evict();
    }

    private void Evict()
    {
        if (Interlocked.Read(ref _memoryBytes) <= MaxMemoryBytes)
        {
            return;
        }

        foreach (KeyValuePair<string, CacheEntry> pair in _memory.OrderBy(p => p.Value.LastAccess))
        {
            if (Interlocked.Read(ref _memoryBytes) <= MaxMemoryBytes)
            {
                break;
            }

            if (_memory.TryRemove(pair.Key, out CacheEntry? removed))
            {
                Interlocked.Add(ref _memoryBytes, -removed.Bytes);
                removed.Bitmap.Dispose();
            }
        }
    }

    private static long EstimateBytes(Bitmap bitmap) =>
        (long)Math.Max(1, bitmap.PixelSize.Width) * Math.Max(1, bitmap.PixelSize.Height) * 4;

    private static int Bucket(int width) => Math.Clamp(((width + 255) / 256) * 256, 256, MaxDecodeWidth);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (CacheEntry entry in _memory.Values)
        {
            entry.Bitmap.Dispose();
        }

        _memory.Clear();
        _http.Dispose();
    }

    private sealed class CacheEntry
    {
        public CacheEntry(Bitmap bitmap, long bytes)
        {
            Bitmap = bitmap;
            Bytes = bytes;
            Touch();
        }

        public Bitmap Bitmap { get; }

        public long Bytes { get; }

        public long LastAccess { get; private set; }

        public void Touch() => LastAccess = Environment.TickCount64;
    }
}
