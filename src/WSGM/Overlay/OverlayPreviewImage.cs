using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using SkiaSharp;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>A bounded, disposable thumbnail that loads only after it is mounted.</summary>
internal sealed class OverlayPreviewImage : Border, IOverlayRefreshable
{
    private static readonly SemaphoreSlim Downloads = new(4);
    private readonly string? _source;
    private readonly Func<CancellationToken, Task<byte[]>>? _read;
    private readonly Stretch _stretch;
    private Bitmap? _bitmap;
    private CancellationTokenSource? _load;

    static OverlayPreviewImage()
    {
        // One class handler instead of a PropertyChanged subscription on every ancestor of every
        // preview: only a visibility change can reveal a preview without changing its viewport.
        IsVisibleProperty.Changed.AddClassHandler<Visual>(OnAnyVisibilityChanged);
    }

    /// <summary>Creates a lazy thumbnail whose load and bitmap lifetime follow mounting and visibility.</summary>
    /// <param name="source">Local path, HTTPS URL or supported data URI; null/empty renders No preview.</param>
    /// <param name="height">Thumbnail height in device-independent pixels.</param>
    /// <param name="stretch">Whether the preview fits its frame or fills it by cropping.</param>
    internal OverlayPreviewImage(string? source, double height = 140, Stretch stretch = Stretch.Uniform)
    {
        _source = source;
        _stretch = stretch;
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
            if (Child is Image image)
            {
                image.Source = null;
            }
        };
    }

    /// <summary>Loads a thumbnail lazily through its existing service rather than exposing file paths.</summary>
    internal OverlayPreviewImage(string key, Func<CancellationToken, Task<byte[]>> read, double height = 140,
        Stretch stretch = Stretch.Uniform) : this(key, height, stretch)
    {
        _read = read;
    }

    /// <inheritdoc />
    /// <remarks>The image source is immutable; resizing keeps the decoded bitmap.</remarks>
    public void RefreshFrom(Control replacement)
    {
        Height = replacement.Height;
    }

    /// <summary>Opens a preview source for decoding without retaining its bytes.</summary>
    /// <param name="source">Local path or file URI, HTTPS URL or image data URI.</param>
    /// <param name="token">Cancels the open or the download.</param>
    /// <returns>A caller-owned readable, seekable stream positioned at the start.</returns>
    /// <exception cref="InvalidDataException">The source is larger than 16 MB.</exception>
    internal static async Task<Stream> OpenAsync(string source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (source.StartsWith("data:image/", StringComparison.Ordinal) &&
            source.Length <= ArtworkDownload.MaximumBytes * 2)
        {
            var bytes = Convert.FromBase64String(source[(source.IndexOf(',') + 1)..]);
            if (bytes.Length > ArtworkDownload.MaximumBytes)
            {
                throw new InvalidDataException("The preview is larger than 16 MB.");
            }

            return new MemoryStream(bytes, false);
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            // Bounds concurrent network fetches; the download itself enforces the 16 MB cap.
            await Downloads.WaitAsync(token);
            try
            {
                return new MemoryStream(await ArtworkDownload.GetAsync(source, token), false);
            }
            finally
            {
                Downloads.Release();
            }
        }

        var path = uri is { IsFile: true } ? uri.LocalPath : source;
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096,
            FileOptions.SequentialScan);
        if (file.Length > ArtworkDownload.MaximumBytes)
        {
            await file.DisposeAsync();
            throw new InvalidDataException("The preview file is larger than 16 MB.");
        }

        return file;
    }

    private static void OnAnyVisibilityChanged(Visual visual, AvaloniaPropertyChangedEventArgs e)
    {
        if (TopLevel.GetTopLevel(visual) is null)
        {
            return;
        }

        foreach (var preview in visual.GetSelfAndVisualDescendants().OfType<OverlayPreviewImage>())
        {
            preview.OnVisibilityChanged();
        }
    }

    private void OnVisibilityChanged()
    {
        // Visibility alone need not change the viewport rectangle. Re-register so the next
        // layout reports it again after a previously hidden page has its actual bounds.
        EffectiveViewportChanged -= LoadWhenVisible;
        if (IsEffectivelyVisible)
        {
            EffectiveViewportChanged += LoadWhenVisible;
        }
        else if (_bitmap is null)
        {
            _load?.Cancel();
            _load?.Dispose();
            _load = null;
        }
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
            var stream = _read is null
                ? await OpenAsync(_source!, token)
                : new MemoryStream(await _read(token), false);
            Bitmap bitmap;
            await using (stream)
            {
                bitmap = await Task.Run(() => Decode(stream), token);
            }

            if (token.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }

            _bitmap = bitmap;
            Child = new Image { Source = bitmap, Stretch = _stretch };
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

    /// <summary>Decodes a bounded thumbnail straight from the source stream.</summary>
    private static Bitmap Decode(Stream stream)
    {
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("Unsupported preview format.");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("The preview dimensions are too large.");
        }

        stream.Position = 0;
        return info.Width >= info.Height
            ? Bitmap.DecodeToWidth(stream, Math.Min(info.Width, 640))
            : Bitmap.DecodeToHeight(stream, Math.Min(info.Height, 640));
    }
}
