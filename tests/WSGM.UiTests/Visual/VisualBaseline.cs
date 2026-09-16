using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using SkiaSharp;
using WSGM.Device.Tests;

namespace WSGM.UiTests.Visual;

internal static class VisualBaseline
{
    /// <summary>The per-channel distance two renders of the same surface may differ by.</summary>
    /// <remarks>
    ///     Skia's antialiasing of a rounded card corner lands one or two levels apart between runs of
    ///     the identical window, which made an exact comparison fail at random on four pixels. Two
    ///     levels of one channel cannot carry a UI change: text, layout, state and colour all move a
    ///     pixel much further than that, and every one of them moves many pixels at once.
    /// </remarks>
    private const int ChannelTolerance = 2;

    internal static void Verify(Window window, string name)
    {
        // Capture resting controls. Focus visuals and caret timing belong to interaction tests.
        window.FocusManager.Focus(null);
        foreach (var visual in window.GetVisualDescendants().OfType<Animatable>())
        {
            visual.Transitions = null;
        }

        window.MouseMove(new Point(-20, -20));
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using MemoryStream stream = new();
        frame.Save(stream, new PngBitmapEncoderOptions());
        var actual = stream.ToArray();
        var artifacts = Path.Combine(RepositoryFiles.Root, "TestResults", "ui", name);
        Directory.CreateDirectory(artifacts);
        File.Delete(Path.Combine(artifacts, "expected.png"));
        File.Delete(Path.Combine(artifacts, "diff.png"));
        File.WriteAllBytes(Path.Combine(artifacts, "actual.png"), actual);
        var baseline = Path.Combine(AppContext.BaseDirectory, "Baselines", name + ".png");
        Assert.True(File.Exists(baseline),
            $"Missing baseline {name}. Review TestResults/ui/{name}/actual.png and use eng/update-ui-baselines.ps1 -Case {name}.");
        var expected = File.ReadAllBytes(baseline);
        File.WriteAllBytes(Path.Combine(artifacts, "expected.png"), expected);
        var mismatch = Compare(expected, actual, out var diff);
        if (diff is not null)
        {
            File.WriteAllBytes(Path.Combine(artifacts, "diff.png"), diff);
        }

        Assert.True(mismatch is null, $"{name}: {mismatch}. Images: {artifacts}");
    }

    internal static string? Compare(byte[] expectedPng, byte[] actualPng, out byte[]? diff)
    {
        using var expected = SKBitmap.Decode(expectedPng) ?? throw new InvalidDataException("Invalid expected PNG");
        using var actual = SKBitmap.Decode(actualPng) ?? throw new InvalidDataException("Invalid actual PNG");
        diff = null;
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            diff = actualPng;
            return
                $"Dimensions differ: expected {expected.Width}x{expected.Height}, actual {actual.Width}x{actual.Height}";
        }

        // Decode both through Skia before comparing, so PNG metadata and compression are irrelevant.
        var left = expected.Pixels;
        var right = actual.Pixels;
        var differences = 0;
        var pixels = new byte[left.Length * 4];
        for (var i = 0; i < left.Length; i++)
        {
            var changed = Differs(left[i], right[i]);
            if (changed)
            {
                differences++;
            }

            pixels[i * 4] = changed ? (byte)255 : (byte)0;
            pixels[i * 4 + 2] = changed ? (byte)255 : (byte)0;
            pixels[i * 4 + 3] = 255;
        }

        if (differences == 0)
        {
            return null;
        }

        using SKBitmap difference =
            new(new SKImageInfo(expected.Width, expected.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        Marshal.Copy(pixels, 0, difference.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(difference);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        diff = encoded.ToArray();
        return $"{differences} pixels differ";
    }

    private static bool Differs(SKColor left, SKColor right)
    {
        return left.Alpha != right.Alpha
               || Math.Abs(left.Red - right.Red) > ChannelTolerance
               || Math.Abs(left.Green - right.Green) > ChannelTolerance
               || Math.Abs(left.Blue - right.Blue) > ChannelTolerance;
    }
}
