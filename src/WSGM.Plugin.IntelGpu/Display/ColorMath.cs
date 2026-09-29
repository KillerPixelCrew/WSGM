namespace WSGM.Plugin.IntelGpu.Display;

/// <summary>The five colour controls, in the integers WSGM publishes.</summary>
/// <param name="Brightness">-25 to 25, zero neutral: an offset of that many hundredths.</param>
/// <param name="Contrast">75 to 125, 100 neutral: a gain in percent.</param>
/// <param name="Gamma">80 to 130, 100 neutral: the curve exponent is 100 divided by it.</param>
/// <param name="Hue">0 to 359 degrees, zero neutral.</param>
/// <param name="Saturation">75 to 125, 100 neutral: a chroma gain in percent.</param>
internal readonly record struct ColorSettings(int Brightness, int Contrast, int Gamma, int Hue, int Saturation)
{
    public const int BrightnessMinimum = -25;
    public const int BrightnessMaximum = 25;
    public const int ContrastMinimum = 75;
    public const int ContrastMaximum = 125;
    public const int GammaMinimum = 80;
    public const int GammaMaximum = 130;
    public const int HueMinimum = 0;
    public const int HueMaximum = 359;
    public const int SaturationMinimum = 75;
    public const int SaturationMaximum = 125;

    /// <summary>The driver's own state: nothing applied.</summary>
    public static ColorSettings Neutral { get; } = new(0, 100, 100, 0, 100);

    /// <summary>Whether the tone curve fields are neutral.</summary>
    public bool NeutralCurve => Brightness == 0 && Contrast == 100 && Gamma == 100;

    /// <summary>Whether the hue and saturation fields are neutral.</summary>
    public bool NeutralMatrix => Hue == 0 && Saturation == 100;
}

/// <summary>
///     The colour algorithms of Intel's IGCL colour sample (<c>Samples/Color_Samples</c>), ported.
/// </summary>
/// <remarks>
///     Brightness, contrast and gamma become one uniformly sampled 1D LUT per channel, applied to the
///     last 1D LUT block of the pipe, as <c>CreateOneDLutFromBCG</c> does: the input times the contrast
///     plus the brightness, clipped to 0-1, raised to the relative gamma. Hue and saturation become a
///     3x3 CSC in BT.709 YCbCr space, as <c>GenerateHueSaturationMatrix</c> does. The sample's own
///     clipping bounds (0.75-1.25 for contrast and relative gamma, ±0.25 for brightness, 0.75-1.25 for
///     saturation) are the published ranges. Neutral settings produce an exact identity, so writing
///     neutral values restores what the driver had before anything was set.
/// </remarks>
internal static class ColorMath
{
    /// <summary>How far a sampled curve may sit from the computed one and still match.</summary>
    public const double Tolerance = 2e-3;

    private static readonly double[,] YCbCrToRgb709 =
    {
        { 1.0000, 0.0000, 1.5748 },
        { 1.0000, -0.1873, -0.4681 },
        { 1.0000, 1.8556, 0.0000 }
    };

    private static readonly double[,] RgbToYCbCr709 =
    {
        { 0.2126, 0.7152, 0.0722 },
        { -0.1146, -0.3854, 0.5000 },
        { 0.5000, -0.4542, -0.0458 }
    };

    /// <summary>Fills one channel of a uniformly sampled LUT.</summary>
    /// <param name="settings">The curve settings.</param>
    /// <param name="channel">The destination, one sample per entry.</param>
    public static void FillCurve(ColorSettings settings, Span<double> channel)
    {
        var count = channel.Length;
        if (count == 0)
        {
            return;
        }

        var contrast = Math.Clamp(settings.Contrast / 100.0, 0.75, 1.25);
        var brightness = Math.Clamp(settings.Brightness / 100.0, -0.25, 0.25);
        var exponent = Math.Clamp(100.0 / settings.Gamma, 0.75, 1.25);
        for (var index = 0; index < count; index++)
        {
            var input = count == 1 ? 1.0 : index / (double)(count - 1);
            if (settings.NeutralCurve)
            {
                channel[index] = input;
                continue;
            }

            var output = Math.Clamp(input * contrast + brightness, 0.0, 1.0);
            channel[index] = Math.Pow(output, exponent);
        }
    }

    /// <summary>Whether what the driver reports matches what WSGM computed.</summary>
    /// <param name="expected">The computed curve channel or matrix.</param>
    /// <param name="actual">What the driver reported, of the same length.</param>
    /// <returns><see langword="true" /> within <see cref="Tolerance" /> at every entry.</returns>
    public static bool Matches(ReadOnlySpan<double> expected, ReadOnlySpan<double> actual)
    {
        if (expected.Length != actual.Length)
        {
            return false;
        }

        for (var index = 0; index < expected.Length; index++)
        {
            if (Math.Abs(expected[index] - actual[index]) > Tolerance)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Builds the hue and saturation matrix, row-major.</summary>
    /// <param name="settings">The hue and saturation settings.</param>
    /// <returns>Nine coefficients.</returns>
    public static double[] HueSaturationMatrix(ColorSettings settings)
    {
        if (settings.NeutralMatrix)
        {
            return [1, 0, 0, 0, 1, 0, 0, 0, 1];
        }

        var saturation = Math.Clamp(settings.Saturation / 100.0, 0.75, 1.25);
        var hue = Math.Clamp(settings.Hue, 0, 359) * Math.PI / 180.0;
        var cosine = Math.Cos(hue);
        var sine = Math.Sin(hue);
        double[,] rotation = { { 1, 0, 0 }, { 0, cosine, -sine }, { 0, sine, cosine } };
        double[,] enhancement = { { 1, 0, 0 }, { 0, saturation, 0 }, { 0, 0, saturation } };
        var result = Multiply(Multiply(Multiply(YCbCrToRgb709, enhancement), rotation), RgbToYCbCr709);
        return
        [
            result[0, 0], result[0, 1], result[0, 2],
            result[1, 0], result[1, 1], result[1, 2],
            result[2, 0], result[2, 1], result[2, 2]
        ];
    }

    private static double[,] Multiply(double[,] left, double[,] right)
    {
        var result = new double[3, 3];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                result[row, column] = left[row, 0] * right[0, column]
                                      + left[row, 1] * right[1, column]
                                      + left[row, 2] * right[2, column];
            }
        }

        return result;
    }
}
