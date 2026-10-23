using System;
using System.IO;
using SkiaSharp;

namespace WSGM.Shell;

/// <summary>Small JPEG covers for trusted configuration chrome; never executes scene content.</summary>
internal static class ScreensaverThumbnail
{
    internal static byte[]? Read(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is <= 0 or > 16 * 1024 * 1024)
        {
            return null;
        }

        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
        {
            return null;
        }

        var scale = Math.Min(1f, 1000f / Math.Max(codec.Info.Width, codec.Info.Height));
        var scaled = codec.GetScaledDimensions(scale);
        if ((long)scaled.Width * scaled.Height > 16 * 1024 * 1024)
        {
            return null;
        }

        using var decoded = new SKBitmap(scaled.Width, scaled.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success)
        {
            return null;
        }

        var rotated = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var width = rotated ? decoded.Height : decoded.Width;
        var height = rotated ? decoded.Width : decoded.Height;
        var ratio = Math.Min(1f, Math.Min(800f / width, 500f / height));
        using var cover = new SKBitmap(Math.Max(1, (int)(width * ratio)), Math.Max(1, (int)(height * ratio)));
        using (var canvas = new SKCanvas(cover))
        {
            canvas.Clear(new SKColor(16, 21, 28));
            canvas.Scale((float)cover.Width / width, (float)cover.Height / height);
            var w = decoded.Width;
            var h = decoded.Height;
            var values = codec.EncodedOrigin switch
            {
                SKEncodedOrigin.TopRight => (-1f, 0f, (float)w, 0f, 1f, 0f),
                SKEncodedOrigin.BottomRight => (-1f, 0f, (float)w, 0f, -1f, (float)h),
                SKEncodedOrigin.BottomLeft => (1f, 0f, 0f, 0f, -1f, (float)h),
                SKEncodedOrigin.LeftTop => (0f, 1f, 0f, 1f, 0f, 0f),
                SKEncodedOrigin.RightTop => (0f, -1f, (float)h, 1f, 0f, 0f),
                SKEncodedOrigin.RightBottom => (0f, -1f, (float)h, -1f, 0f, (float)w),
                SKEncodedOrigin.LeftBottom => (0f, 1f, 0f, -1f, 0f, (float)w),
                _ => (1f, 0f, 0f, 0f, 1f, 0f)
            };
            var orientation = new SKMatrix
            {
                ScaleX = values.Item1, SkewX = values.Item2, TransX = values.Item3,
                SkewY = values.Item4, ScaleY = values.Item5, TransY = values.Item6, Persp2 = 1
            };
            canvas.Concat(in orientation);
            canvas.DrawBitmap(decoded, 0, 0);
        }

        using var image = SKImage.FromBitmap(cover);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 78);
        if (encoded is not null && encoded.Size <= 64 * 1024)
        {
            return encoded.ToArray();
        }

        using var reduced = new SKBitmap(Math.Max(1, cover.Width / 2), Math.Max(1, cover.Height / 2));
        using (var canvas = new SKCanvas(reduced))
        {
            canvas.DrawBitmap(cover, new SKRect(0, 0, reduced.Width, reduced.Height));
        }

        using var small = SKImage.FromBitmap(reduced);
        using var compressed = small.Encode(SKEncodedImageFormat.Jpeg, 60);
        return compressed is not null && compressed.Size <= 64 * 1024 ? compressed.ToArray() : null;
    }
}
