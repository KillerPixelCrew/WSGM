using SkiaSharp;

namespace WSGM.UiTests;

public sealed class VisualComparisonTests
{
    [Fact]
    public void EqualDecodedPixelsPass()
    {
        Assert.Null(VisualBaseline.Compare(Png(2, SKColors.Black), Png(2, SKColors.Black), out byte[]? diff));
        Assert.Null(diff);
    }

    [Fact]
    public void OneChangedPixelFailsAndProducesADiff()
    {
        string? result = VisualBaseline.Compare(Png(2, SKColors.Black), Png(2, SKColors.Red), out byte[]? diff);
        Assert.Equal("1 pixels differ", result);
        Assert.NotNull(diff);
        using var image = SKBitmap.Decode(diff);
        Assert.Equal(SKColors.Magenta, image.GetPixel(0, 0));
        Assert.Equal(SKColors.Black, image.GetPixel(1, 0));
    }

    [Fact]
    public void ChangedDimensionsFail()
    {
        Assert.StartsWith("Dimensions differ", VisualBaseline.Compare(Png(2, SKColors.Black), Png(3, SKColors.Black), out _));
    }

    private static byte[] Png(int width, SKColor first)
    {
        using SKBitmap bitmap = new(width, 1);
        bitmap.Erase(SKColors.Black);
        bitmap.SetPixel(0, 0, first);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
