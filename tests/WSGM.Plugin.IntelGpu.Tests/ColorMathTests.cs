using WSGM.Plugin.IntelGpu.Display;
using Xunit;

namespace WSGM.Plugin.IntelGpu.Tests;

public sealed class ColorMathTests
{
    [Fact]
    public void NeutralSettingsProduceALinearRamp()
    {
        var channel = new double[1024];

        ColorMath.FillCurve(ColorSettings.Neutral, channel);

        for (var index = 0; index < channel.Length; index++)
        {
            Assert.Equal(index / 1023.0, channel[index], 12);
        }
    }

    [Fact]
    public void NeutralSettingsProduceTheExactIdentityMatrix()
    {
        Assert.Equal([1d, 0, 0, 0, 1, 0, 0, 0, 1], ColorMath.HueSaturationMatrix(ColorSettings.Neutral));
    }

    [Fact]
    public void ContrastAndBrightnessFollowIntelsSample()
    {
        // CreateOneDLutFromBCG: input times contrast plus brightness, clipped, raised to the relative gamma.
        var channel = new double[3];

        ColorMath.FillCurve(ColorSettings.Neutral with { Contrast = 110, Brightness = 5 }, channel);

        Assert.Equal(0.05, channel[0], 9);
        Assert.Equal(0.6, channel[1], 9);
        Assert.Equal(1.0, channel[2], 9);
    }

    [Fact]
    public void GammaAboveNeutralLiftsTheMidtones()
    {
        var neutral = new double[3];
        var lifted = new double[3];

        ColorMath.FillCurve(ColorSettings.Neutral, neutral);
        ColorMath.FillCurve(ColorSettings.Neutral with { Gamma = 120 }, lifted);

        Assert.True(lifted[1] > neutral[1]);
        Assert.Equal(Math.Pow(0.5, 100.0 / 120), lifted[1], 9);
    }

    [Fact]
    public void ACurveIsRecognisedOnlyWhenItMatches()
    {
        var settings = ColorSettings.Neutral with { Contrast = 90 };
        var channel = new double[256];
        ColorMath.FillCurve(settings, channel);

        Assert.True(ColorMath.CurveMatches(settings, channel));
        Assert.False(ColorMath.CurveMatches(ColorSettings.Neutral, channel));
    }

    [Fact]
    public void SaturationWithoutHueShiftKeepsGreyGrey()
    {
        var settings = ColorSettings.Neutral with { Saturation = 120 };
        var matrix = ColorMath.HueSaturationMatrix(settings);

        // A grey pixel has no chroma, so every row must sum to one whatever the saturation.
        for (var row = 0; row < 3; row++)
        {
            Assert.Equal(1.0, matrix[row * 3] + matrix[row * 3 + 1] + matrix[row * 3 + 2], 3);
        }

        Assert.True(ColorMath.MatrixMatches(settings, matrix));
        Assert.False(ColorMath.MatrixMatches(ColorSettings.Neutral, matrix));
    }
}
