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
    public void AntialiasingJitterWithinTwoLevelsPasses()
    {
        SKColor jittered = new(0x2A, 0x2A, 0x2A);
        Assert.Null(VisualBaseline.Compare(Png(2, new SKColor(0x2C, 0x2C, 0x2C)), Png(2, jittered), out _));
    }

    [Fact]
    public void AThirdLevelOfChannelDifferenceStillFails()
    {
        Assert.Equal(
            "1 pixels differ",
            VisualBaseline.Compare(Png(2, new SKColor(0x2D, 0x2A, 0x2A)), Png(2, new SKColor(0x2A, 0x2A, 0x2A)), out _));
    }

    [Fact]
    public void TransparencyIsNeverToleratedBecauseItIsNotAntialiasing()
    {
        Assert.Equal(
            "1 pixels differ",
            VisualBaseline.Compare(
                Png(2, new SKColor(0x2A, 0x2A, 0x2A, 0xFE)),
                Png(2, new SKColor(0x2A, 0x2A, 0x2A)),
                out _));
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
