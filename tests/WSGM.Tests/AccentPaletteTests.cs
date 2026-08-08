using Avalonia.Media;
using WSGM.Themes;

namespace WSGM.Tests;

/// <summary>The executable specification of the pure accent-pipeline pieces:
/// <see cref="AccentPalette.Parse"/> fallback behavior and the relative-luminance
/// black/white foreground decision. The Application-mutating Apply path is
/// device/manual-verified, not unit-tested (no Avalonia app in tests).</summary>
public sealed class AccentPaletteTests
{
    [Fact]
    public void Parse_ValidSixDigitHex_ReturnsColor()
    {
        var color = AccentPalette.Parse("#336699");

        Assert.Equal(new Color(0xFF, 0x33, 0x66, 0x99), color);
    }

    [Fact]
    public void Parse_ValidEightDigitHex_KeepsAlpha()
    {
        var color = AccentPalette.Parse("#80FF0000");

        Assert.Equal(new Color(0x80, 0xFF, 0x00, 0x00), color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a color")]
    [InlineData("#GGHHII")]
    [InlineData("#12345")]
    public void Parse_MissingOrInvalid_FallsBackToDefaultAccent(string? value)
    {
        var color = AccentPalette.Parse(value);

        Assert.Equal(Color.Parse(AccentPalette.DefaultAccent), color);
    }

    [Fact]
    public void Parse_DefaultAccent_IsTheClassicOrange()
    {
        Assert.Equal(new Color(0xFF, 0xFF, 0x9D, 0x3D), AccentPalette.Parse(AccentPalette.DefaultAccent));
    }

    [Fact]
    public void UseBlackForeground_DefaultOrangeAccent_PicksBlack()
    {
        // Pins the shipped Palette.axaml pairing: HcOnAccentBrush is Black on the
        // classic orange accent.
        Assert.True(AccentPalette.UseBlackForeground(AccentPalette.Parse(AccentPalette.DefaultAccent)));
    }

    [Theory]
    [InlineData(0xFF, 0xFF, 0xFF)] // white
    [InlineData(0xFF, 0xD7, 0x00)] // gold
    [InlineData(0x00, 0xFF, 0x00)] // pure green (high luminance)
    [InlineData(0x80, 0x80, 0x80)] // mid gray (~0.216 luminance, above threshold)
    public void UseBlackForeground_BrightAccents_PickBlack(byte r, byte g, byte b)
    {
        Assert.True(AccentPalette.UseBlackForeground(new Color(0xFF, r, g, b)));
    }

    [Theory]
    [InlineData(0x00, 0x00, 0x00)] // black
    [InlineData(0x00, 0x00, 0xFF)] // pure blue (~0.072 luminance)
    [InlineData(0x8B, 0x00, 0x00)] // dark red
    [InlineData(0x5A, 0x00, 0x8C)] // deep purple
    public void UseBlackForeground_DarkAccents_PickWhite(byte r, byte g, byte b)
    {
        Assert.False(AccentPalette.UseBlackForeground(new Color(0xFF, r, g, b)));
    }

    [Fact]
    public void RelativeLuminance_BlackAndWhite_AreTheExtremes()
    {
        Assert.Equal(0.0, AccentPalette.RelativeLuminance(Colors.Black), 10);
        Assert.Equal(1.0, AccentPalette.RelativeLuminance(Colors.White), 10);
    }

    [Fact]
    public void UseBlackForeground_ThresholdIsRelativeLuminance()
    {
        // Contrast-equality threshold: black beats white above L = 0.1791.
        var justAbove = new Color(0xFF, 0x00, 0x80, 0x00); // green 0x80 → L ≈ 0.154 — below
        Assert.False(AccentPalette.UseBlackForeground(justAbove));
        var brighter = new Color(0xFF, 0x00, 0x90, 0x00); // green 0x90 → L ≈ 0.201 — above
        Assert.True(AccentPalette.UseBlackForeground(brighter));
    }
}
