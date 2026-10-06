using Avalonia;
using SkiaSharp;

namespace WSGM.UiTests.Visual;

public sealed class VisualComparisonTests
{
    [Fact]
    public void EqualDecodedPixelsPass()
    {
        Assert.Null(VisualBaseline.Compare(Png(2, SKColors.Black), Png(2, SKColors.Black), out var diff));
        Assert.Null(diff);
    }

    [Fact]
    public void OneChangedPixelFailsAndProducesADiff()
    {
        var result = VisualBaseline.Compare(Png(2, SKColors.Black), Png(2, SKColors.Red), out var diff);
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
            VisualBaseline.Compare(Png(2, new SKColor(0x2D, 0x2A, 0x2A)), Png(2, new SKColor(0x2A, 0x2A, 0x2A)),
                out _));
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
        Assert.StartsWith("Dimensions differ",
            VisualBaseline.Compare(Png(2, SKColors.Black), Png(3, SKColors.Black), out _));
    }

    [Fact]
    public void ArcRasterNoiseIsLimitedToTheExplicitIconRegion()
    {
        var expected = Png(2, new SKColor(55, 59, 65));
        var actual = Png(2, new SKColor(66, 70, 75));
        Assert.Null(VisualBaseline.Compare(expected, actual, out _, new Rect(0, 0, 1, 1)));
        Assert.Equal("1 pixels differ",
            VisualBaseline.Compare(expected, actual, out _, new Rect(1, 0, 1, 1)));
    }

    [Fact]
    public void ExplicitRasterRegionStillRejectsTransparencyChanges()
    {
        Assert.Equal("1 pixels differ", VisualBaseline.Compare(
            Png(2, new SKColor(42, 42, 42, 254)), Png(2, new SKColor(42, 42, 42)), out _,
            new Rect(0, 0, 2, 1)));
    }

    private static byte[] Png(int width, SKColor first)
    {
        using SKBitmap bitmap = new(width, 1);
        bitmap.Erase(SKColors.Black);
        bitmap.SetPixel(0, 0, first);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
