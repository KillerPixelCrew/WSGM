using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>A bounded, disposable thumbnail that loads only after it is mounted.</summary>
internal sealed class OverlayPreviewImage : Border, IOverlayRefreshable
{
    private static readonly SemaphoreSlim Downloads = new(4);
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, byte[]> Cache = [];
    private static int _cachedBytes;
    private readonly string? _source;
    private Bitmap? _bitmap;
    private CancellationTokenSource? _load;

    internal OverlayPreviewImage(string? source, double height = 140)
    {
        _source = source;
        Height = height;
        Child = new TextBlock
        {
            Text = string.IsNullOrEmpty(source) ? "No preview" : "Loading preview…",
            VerticalAlignment = VerticalAlignment.Center
        };
        // The effective viewport changes when a scroll, a layout or a shown page brings the image
        // into view, and only for this control, unlike a tree-wide layout pass.
        AttachedToVisualTree += (_, _) => EffectiveViewportChanged += LoadWhenVisible;
        DetachedFromVisualTree += (_, _) =>
        {
            EffectiveViewportChanged -= LoadWhenVisible;
            _load?.Cancel();
            _load?.Dispose();
            _load = null;
            _bitmap?.Dispose();
            _bitmap = null;
        };
    }

    public void RefreshFrom(Control replacement)
    {
    }

    private void LoadWhenVisible(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (_load is not null || _source is null || !IsEffectivelyVisible || Bounds.Width <= 0
            || !e.EffectiveViewport.Intersects(new Rect(Bounds.Size)))
        {
            return;
        }

        _load = new CancellationTokenSource();
        _ = LoadAsync(_load.Token);
    }

    private async Task LoadAsync(CancellationToken token)
    {
        try
        {
            var bytes = await ReadAsync(_source!, token);
            var bitmap = await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes, false);
                using var codec = SKCodec.Create(stream) ??
                                  throw new InvalidDataException("Unsupported preview format.");
                var info = codec.Info;
                if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > 64 * 1024 * 1024)
                {
                    throw new InvalidDataException("The preview dimensions are too large.");
                }

                stream.Position = 0;
                return info.Width >= info.Height
                    ? Bitmap.DecodeToWidth(stream, Math.Min(info.Width, 640))
                    : Bitmap.DecodeToHeight(stream, Math.Min(info.Height, 640));
            }, token);
            if (token.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }

            _bitmap = bitmap;
            Child = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Child = new TextBlock { Text = "Preview unavailable", TextWrapping = TextWrapping.Wrap };
                ToolTip.SetTip(this, ex.Message);
            }
        }
    }

    internal static async Task<byte[]> ReadAsync(string source, CancellationToken token)
    {
        lock (CacheGate)
        {
            if (Cache.TryGetValue(source, out var cached))
            {
                return cached;
            }
        }

        await Downloads.WaitAsync(token);
        try
        {
            byte[] bytes;
            if (source.StartsWith("data:image/", StringComparison.Ordinal) &&
                source.Length <= ArtworkDownload.MaximumBytes * 2)
            {
                bytes = Convert.FromBase64String(source[(source.IndexOf(',') + 1)..]);
            }
            else if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                bytes = await ArtworkDownload.GetAsync(source, token);
            }
            else
            {
                var path = Uri.TryCreate(source, UriKind.Absolute, out uri) && uri.IsFile ? uri.LocalPath : source;
                var info = new FileInfo(path);
                if (info.Length > ArtworkDownload.MaximumBytes)
                {
                    throw new InvalidDataException("The preview file is larger than 16 MB.");
                }

                bytes = await File.ReadAllBytesAsync(path, token);
            }

            if (bytes.Length > ArtworkDownload.MaximumBytes)
            {
                throw new InvalidDataException("The preview is larger than 16 MB.");
            }

            lock (CacheGate)
            {
                if (!Cache.ContainsKey(source))
                {
                    while (Cache.Count >= 64 || _cachedBytes + bytes.Length > 64 * 1024 * 1024)
                    {
                        var first = Cache.First();
                        _cachedBytes -= first.Value.Length;
                        Cache.Remove(first.Key);
                    }

                    Cache[source] = bytes;
                    _cachedBytes += bytes.Length;
                }
            }

            return bytes;
        }
        finally
        {
            Downloads.Release();
        }
    }
}
