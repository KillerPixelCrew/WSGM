using System.IO.Compression;
using WSGM.Core;

namespace WSGM.Tests;

/// <summary>Round-trip and robustness coverage for .wsgmsplash theme export/import
/// (SplashTheme): bundled images, config-only archives, and archives that must be
/// rejected with null instead of an exception.</summary>
public sealed class SplashThemeTests : IDisposable
{
    private readonly string _root;
    private readonly string _sourceDir;
    private readonly string _targetDir;

    public SplashThemeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wsgm-splash-theme-tests", Guid.NewGuid().ToString("N"));
        _sourceDir = Path.Combine(_root, "source");
        _targetDir = Path.Combine(_root, "target");
        Directory.CreateDirectory(_sourceDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }

    private static SplashConfig FullyCustomized(string logoPath, string backgroundPath) =>
        new()
        {
            Text = "WSGM",
            TextEnabled = false,
            TextColor = "#FF9D3D",
            TitleFontSize = 48,
            Caption = "STARTING STEAM",
            CaptionColor = "#AAAAAA",
            CaptionFontSize = 14,
            SpinnerStyle = SplashSpinnerStyle.SweepLine,
            SpinnerColor = "#00FF00",
            SpinnerSize = 72,
            SweepEdge = SweepEdge.Top,
            BackgroundColor = "#101010",
            VignetteEnabled = true,
            BackgroundImagePath = backgroundPath,
            LogoImagePath = logoPath,
            LogoMaxSize = 320,
            TextPlacement = new SplashElementPlacement
            {
                Mode = SplashPlacementMode.Anchor,
                Anchor = SplashPlacementAnchor.BottomLeft,
                PaddingX = 48,
                PaddingY = 160,
            },
            SpinnerPlacement = new SplashElementPlacement
            {
                Mode = SplashPlacementMode.Absolute,
                X = 640,
                Y = 360,
            },
            LogoPlacement = new SplashElementPlacement
            {
                Mode = SplashPlacementMode.Anchor,
                Anchor = SplashPlacementAnchor.TopCenter,
                PaddingX = 0,
                PaddingY = 96,
            },
        };

    private static void AssertNonImageFieldsEqual(SplashConfig expected, SplashConfig actual)
    {
        Assert.Equal(expected.Text, actual.Text);
        Assert.Equal(expected.TextEnabled, actual.TextEnabled);
        Assert.Equal(expected.TextColor, actual.TextColor);
        Assert.Equal(expected.TitleFontSize, actual.TitleFontSize);
        Assert.Equal(expected.Caption, actual.Caption);
        Assert.Equal(expected.CaptionColor, actual.CaptionColor);
        Assert.Equal(expected.CaptionFontSize, actual.CaptionFontSize);
        Assert.Equal(expected.SpinnerStyle, actual.SpinnerStyle);
        Assert.Equal(expected.SpinnerColor, actual.SpinnerColor);
        Assert.Equal(expected.SpinnerSize, actual.SpinnerSize);
        Assert.Equal(expected.SweepEdge, actual.SweepEdge);
        Assert.Equal(expected.BackgroundColor, actual.BackgroundColor);
        Assert.Equal(expected.VignetteEnabled, actual.VignetteEnabled);
        Assert.Equal(expected.LogoMaxSize, actual.LogoMaxSize);
        AssertPlacementEqual(expected.TextPlacement, actual.TextPlacement);
        AssertPlacementEqual(expected.SpinnerPlacement, actual.SpinnerPlacement);
        AssertPlacementEqual(expected.LogoPlacement, actual.LogoPlacement);
    }

    private static void AssertPlacementEqual(SplashElementPlacement expected, SplashElementPlacement actual)
    {
        Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.Anchor, actual.Anchor);
        Assert.Equal(expected.PaddingX, actual.PaddingX);
        Assert.Equal(expected.PaddingY, actual.PaddingY);
        Assert.Equal(expected.X, actual.X);
        Assert.Equal(expected.Y, actual.Y);
    }

    [Fact]
    public void FullRoundTripWithImagesPreservesEveryFieldAndExtractsTheImages()
    {
        var logoBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var backgroundBytes = new byte[] { 0xFF, 0xD8, 0xFF, 9, 8, 7, 6, 5 };
        var logoPath = Path.Combine(_sourceDir, "my logo.PNG");
        var backgroundPath = Path.Combine(_sourceDir, "wallpaper.jpg");
        File.WriteAllBytes(logoPath, logoBytes);
        File.WriteAllBytes(backgroundPath, backgroundBytes);
        var original = FullyCustomized(logoPath, backgroundPath);
        var themePath = Path.Combine(_root, "custom.wsgmsplash");

        Assert.True(SplashTheme.Export(original, themePath));
        var imported = SplashTheme.Import(themePath, _targetDir);

        Assert.NotNull(imported);
        AssertNonImageFieldsEqual(original, imported);
        Assert.Equal(Path.Combine(_targetDir, "logo.png"), imported.LogoImagePath);
        Assert.Equal(Path.Combine(_targetDir, "background.jpg"), imported.BackgroundImagePath);
        Assert.Equal(logoBytes, File.ReadAllBytes(imported.LogoImagePath));
        Assert.Equal(backgroundBytes, File.ReadAllBytes(imported.BackgroundImagePath));
        // Exporting again must leave the caller's config untouched.
        Assert.Equal(logoPath, original.LogoImagePath);
        Assert.Equal(backgroundPath, original.BackgroundImagePath);
    }

    [Fact]
    public void ExportBundlesImagesUnderDeterministicEntryNames()
    {
        var logoPath = Path.Combine(_sourceDir, "Some Fancy Mark.png");
        var backgroundPath = Path.Combine(_sourceDir, "photo.JPEG");
        File.WriteAllBytes(logoPath, [1]);
        File.WriteAllBytes(backgroundPath, [2]);
        var themePath = Path.Combine(_root, "named.wsgmsplash");

        Assert.True(
            SplashTheme.Export(
                new SplashConfig { LogoImagePath = logoPath, BackgroundImagePath = backgroundPath },
                themePath
            )
        );

        using var archive = ZipFile.OpenRead(themePath);
        var names = archive.Entries.Select(e => e.FullName).OrderBy(n => n).ToArray();
        Assert.Equal(["background.jpeg", "logo.png", "splash.json"], names);
    }

    [Fact]
    public void ConfigOnlyThemeRoundTripsWithoutImages()
    {
        var original = FullyCustomized(logoPath: "", backgroundPath: "");
        var themePath = Path.Combine(_root, "plain.wsgmsplash");

        Assert.True(SplashTheme.Export(original, themePath));
        var imported = SplashTheme.Import(themePath, _targetDir);

        Assert.NotNull(imported);
        AssertNonImageFieldsEqual(original, imported);
        Assert.Equal("", imported.LogoImagePath);
        Assert.Equal("", imported.BackgroundImagePath);
        Assert.False(Directory.Exists(_targetDir));
    }

    [Fact]
    public void ExportSkipsAMissingImageFileAndImportKeepsThePathString()
    {
        var danglingPath = Path.Combine(_sourceDir, "deleted-logo.png");
        var themePath = Path.Combine(_root, "dangling.wsgmsplash");

        Assert.True(SplashTheme.Export(new SplashConfig { LogoImagePath = danglingPath }, themePath));
        var imported = SplashTheme.Import(themePath, _targetDir);

        Assert.NotNull(imported);
        Assert.Equal(danglingPath, imported.LogoImagePath);
        Assert.False(Directory.Exists(_targetDir));
    }

    [Fact]
    public void MalformedZipReturnsNullInsteadOfThrowing()
    {
        var themePath = Path.Combine(_root, "garbage.wsgmsplash");
        File.WriteAllBytes(themePath, [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01]);

        Assert.Null(SplashTheme.Import(themePath, _targetDir));
    }

    [Fact]
    public void NonexistentFileReturnsNullInsteadOfThrowing()
    {
        Assert.Null(SplashTheme.Import(Path.Combine(_root, "never-written.wsgmsplash"), _targetDir));
    }

    [Fact]
    public void ZipWithoutSplashJsonReturnsNull()
    {
        var themePath = Path.Combine(_root, "no-config.wsgmsplash");
        using (var archive = ZipFile.Open(themePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("logo.png");
            using var stream = entry.Open();
            stream.Write([1, 2, 3]);
        }

        Assert.Null(SplashTheme.Import(themePath, _targetDir));
    }

    [Fact]
    public void MalformedSplashJsonReturnsNull()
    {
        var themePath = Path.Combine(_root, "bad-json.wsgmsplash");
        using (var archive = ZipFile.Open(themePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("splash.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("{ not json at all");
        }

        Assert.Null(SplashTheme.Import(themePath, _targetDir));
    }

    [Fact]
    public void ImportRewritesImagePathsIntoTheTargetDirectoryAndRepairsExplicitNulls()
    {
        var themePath = Path.Combine(_root, "handmade.wsgmsplash");
        using (var archive = ZipFile.Open(themePath, ZipArchiveMode.Create))
        {
            var config = archive.CreateEntry("splash.json");
            using (var writer = new StreamWriter(config.Open()))
            {
                // Hand-edited theme: null text and image paths pointing at the
                // author's machine — import must repair the nulls and rewrite the
                // paths to the extracted copies.
                writer.Write(
                    """{ "Text": null, "LogoImagePath": "C:\\Users\\author\\logo.png", "BackgroundImagePath": "C:\\Users\\author\\bg.png" }"""
                );
            }
            var logo = archive.CreateEntry("logo.png");
            using (var stream = logo.Open())
            {
                stream.Write([10, 20]);
            }
            var background = archive.CreateEntry("background.png");
            using (var stream = background.Open())
            {
                stream.Write([30, 40]);
            }
        }

        var imported = SplashTheme.Import(themePath, _targetDir);

        Assert.NotNull(imported);
        Assert.Equal("Please wait", imported.Text);
        Assert.Equal(Path.Combine(_targetDir, "logo.png"), imported.LogoImagePath);
        Assert.Equal(Path.Combine(_targetDir, "background.png"), imported.BackgroundImagePath);
        Assert.True(File.Exists(imported.LogoImagePath));
        Assert.True(File.Exists(imported.BackgroundImagePath));
    }
}
