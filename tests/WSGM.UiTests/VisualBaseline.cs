using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using SkiaSharp;

namespace WSGM.UiTests;

internal static class VisualBaseline
{
    internal static void Verify(Window window, string name)
    {
        foreach (var visual in window.GetVisualDescendants().OfType<Avalonia.Animation.Animatable>())
        {
            visual.Transitions = null;
        }
        window.MouseMove(new Avalonia.Point(-20, -20));
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using MemoryStream stream = new();
        frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        byte[] actual = stream.ToArray();
        string artifacts = Path.Combine(RepositoryRoot(), "TestResults", "ui", name);
        Directory.CreateDirectory(artifacts);
        File.Delete(Path.Combine(artifacts, "expected.png"));
        File.Delete(Path.Combine(artifacts, "diff.png"));
        File.WriteAllBytes(Path.Combine(artifacts, "actual.png"), actual);
        string baseline = Path.Combine(AppContext.BaseDirectory, "Baselines", name + ".png");
        Assert.True(File.Exists(baseline), $"Missing baseline {name}. Review TestResults/ui/{name}/actual.png and use eng/update-ui-baselines.ps1 -Case {name}.");
        byte[] expected = File.ReadAllBytes(baseline);
        File.WriteAllBytes(Path.Combine(artifacts, "expected.png"), expected);
        string? mismatch = Compare(expected, actual, out byte[]? diff);
        if (diff is not null) { File.WriteAllBytes(Path.Combine(artifacts, "diff.png"), diff); }
        Assert.True(mismatch is null, $"{name}: {mismatch}. Images: {artifacts}");
    }

    internal static string? Compare(byte[] expectedPng, byte[] actualPng, out byte[]? diff)
    {
        using SKBitmap expected = SKBitmap.Decode(expectedPng) ?? throw new InvalidDataException("Invalid expected PNG");
        using SKBitmap actual = SKBitmap.Decode(actualPng) ?? throw new InvalidDataException("Invalid actual PNG");
        diff = null;
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            diff = actualPng;
            return $"Dimensions differ: expected {expected.Width}x{expected.Height}, actual {actual.Width}x{actual.Height}";
        }
        // Decode both through Skia before comparing, so PNG metadata and compression are irrelevant.
        SKColor[] left = expected.Pixels;
        SKColor[] right = actual.Pixels;
        int differences = 0;
        byte[] pixels = new byte[left.Length * 4];
        for (int i = 0; i < left.Length; i++)
        {
            bool changed = left[i] != right[i];
            if (changed) { differences++; }
            pixels[i * 4] = changed ? (byte)255 : (byte)0;
            pixels[i * 4 + 2] = changed ? (byte)255 : (byte)0;
            pixels[i * 4 + 3] = 255;
        }
        if (differences == 0) { return null; }
        using SKBitmap difference = new(new SKImageInfo(expected.Width, expected.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        Marshal.Copy(pixels, 0, difference.GetPixels(), pixels.Length);
        using SKImage image = SKImage.FromBitmap(difference);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        diff = encoded.ToArray();
        return $"{differences} pixels differ";
    }

    internal static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WSGM.slnx"))) { return directory.FullName; }
        }
        throw new DirectoryNotFoundException("Run UI tests from a WSGM checkout");
    }
}
