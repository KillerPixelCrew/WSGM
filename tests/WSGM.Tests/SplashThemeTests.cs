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
    public void ExportSkipsAMissingImageFileAndImportDropsTheDanglingPath()
    {
        var danglingPath = Path.Combine(_sourceDir, "deleted-logo.png");
        var themePath = Path.Combine(_root, "dangling.wsgmsplash");

        Assert.True(SplashTheme.Export(new SplashConfig { LogoImagePath = danglingPath }, themePath));
        var imported = SplashTheme.Import(themePath, _targetDir);

        Assert.NotNull(imported);
        Assert.Equal("", imported.LogoImagePath);
        Assert.False(Directory.Exists(_targetDir));
    }

    [Theory]
    [InlineData(@"\\attacker-host\share\logo.png", @"\\attacker-host\share\bg.png")]
    [InlineData(@"C:\Users\author\logo.png", @"D:\shared\bg.png")]
    [InlineData(@"..\..\secrets\logo.png", "bg.png")]
    public void ImagePathsFromTheJsonWithoutAMatchingEntryImportAsEmpty(string logoPath, string backgroundPath)
    {
        var themePath = Path.Combine(_root, "path-only.wsgmsplash");
        using (var archive = ZipFile.Open(themePath, ZipArchiveMode.Create))
        {
            WriteJsonEntry(
                archive,
                $$"""
                { "LogoImagePath": "{{logoPath.Replace(@"\", @"\\")}}",
                  "BackgroundImagePath": "{{backgroundPath.Replace(@"\", @"\\")}}" }
                """
            );
        }

        var imported = SplashTheme.Import(themePath, _targetDir);

        Assert.NotNull(imported);
        Assert.Equal("", imported.LogoImagePath);
        Assert.Equal("", imported.BackgroundImagePath);
        Assert.False(Directory.Exists(_targetDir));
    }

    [Fact]
    public void AJsonPathIsIgnoredEvenWhenTheArchiveAlsoCarriesTheEntry()
    {
        var themePath = Path.Combine(_root, "path-and-entry.wsgmsplash");
        using (var archive = ZipFile.Open(themePath, ZipArchiveMode.Create))
        {
            WriteJsonEntry(
                archive,
                """{ "LogoImagePath": "\\\\attacker-host\\share\\logo.png", "BackgroundImagePath": "\\\\attacker-host\\share\\bg.png" }"""
            );
            var logo = archive.CreateEntry("logo.png");
            using (var stream = logo.Open())
            {
                stream.Write([7, 7]);
            }
            var background = archive.CreateEntry("background.png");
            using (var stream = background.Open())
            {
                stream.Write([8, 8]);
            }
        }

        var imported = SplashTheme.Import(themePath, _targetDir);

        Assert.NotNull(imported);
        Assert.Equal(Path.Combine(_targetDir, "logo.png"), imported.LogoImagePath);
        Assert.Equal(Path.Combine(_targetDir, "background.png"), imported.BackgroundImagePath);
        Assert.Equal([7, 7], File.ReadAllBytes(imported.LogoImagePath));
        Assert.Equal([8, 8], File.ReadAllBytes(imported.BackgroundImagePath));
    }

    [Fact]
    public void ExportRefusesAnOversizedImageAndLeavesTheExistingDestinationUntouched()
    {
        var destination = Path.Combine(_root, "existing-theme.wsgmsplash");
        var originalBytes = new byte[] { 0x50, 0x4B, 5, 6, 7 };
        File.WriteAllBytes(destination, originalBytes);
        var hugePath = Path.Combine(_sourceDir, "huge.png");
        using (var file = File.Create(hugePath))
        {
            // One byte past the per-image cap the importer enforces.
            file.SetLength(64L * 1024 * 1024 + 1);
        }

        Assert.False(SplashTheme.Export(new SplashConfig { LogoImagePath = hugePath }, destination));

        Assert.Equal(originalBytes, File.ReadAllBytes(destination));
        Assert.Equal([destination], Directory.GetFiles(_root));
    }

    [Fact]
    public void ExportRefusesAnUnsupportedImageExtensionAndLeavesTheExistingDestinationUntouched()
    {
        var destination = Path.Combine(_root, "existing-theme.wsgmsplash");
        var originalBytes = new byte[] { 0x50, 0x4B, 3, 4, 8 };
        File.WriteAllBytes(destination, originalBytes);
        // A format the picker may offer but the importer's entry-name whitelist
        // rejects — bundling it would produce an archive nobody can import.
        var webpPath = Path.Combine(_sourceDir, "modern.webp");
        File.WriteAllBytes(webpPath, [1, 2, 3, 4]);

        Assert.False(SplashTheme.Export(new SplashConfig { LogoImagePath = webpPath }, destination));

        Assert.Equal(originalBytes, File.ReadAllBytes(destination));
        Assert.Equal([destination], Directory.GetFiles(_root));
    }

    [Fact]
    public void ExportRefusesAnUnsupportedBackgroundExtensionAndWritesNoArchiveAtAll()
    {
        var destination = Path.Combine(_root, "never-written.wsgmsplash");
        var tiffPath = Path.Combine(_sourceDir, "photo.tiff");
        File.WriteAllBytes(tiffPath, [9, 9]);

        Assert.False(
            SplashTheme.Export(new SplashConfig { BackgroundImagePath = tiffPath }, destination)
        );

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(_root));
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
    public void OversizedImageEntryIsRejectedWithoutCreatingTheTargetDirectory()
    {
        var themePath = Path.Combine(_root, "huge-image.wsgmsplash");
        using (var archive = ZipFile.Open(themePath, ZipArchiveMode.Create))
        {
            WriteJsonEntry(archive, "{}");
            var logo = archive.CreateEntry("logo.png");
            using var stream = logo.Open();
            var chunk = new byte[1024 * 1024];
            for (var written = 0L; written <= 64L * 1024 * 1024; written += chunk.Length)
            {
                stream.Write(chunk);
            }
        }

        Assert.Null(SplashTheme.Import(themePath, _targetDir));
        Assert.False(Directory.Exists(_targetDir));
    }

    [Fact]
    public void OversizedSplashJsonEntryIsRejected()
    {
        var themePath = Path.Combine(_root, "huge-json.wsgmsplash");
        using (var archive = ZipFile.Open(themePath, ZipArchiveMode.Create))
        {
            WriteJsonEntry(archive, "{}" + new string(' ', 2 * 1024 * 1024));
        }

        Assert.Null(SplashTheme.Import(themePath, _targetDir));
        Assert.False(Directory.Exists(_targetDir));
    }

    [Theory]
    [InlineData("../logo.png")]
    [InlineData("..\\logo.png")]
    [InlineData("images/logo.png")]
    [InlineData("notes.txt")]
    [InlineData("logo.exe")]
    [InlineData("background.png.exe")]
    public void TraversalOrUnknownEntryNameRejectsTheWholeArchive(string entryName)
    {
        var themePath = Path.Combine(_root, "hostile.wsgmsplash");
        using (var archive = ZipFile.Open(themePath, ZipArchiveMode.Create))
        {
            WriteJsonEntry(archive, "{}");
            var entry = archive.CreateEntry(entryName);
            using var stream = entry.Open();
            stream.Write([1, 2, 3]);
        }

        Assert.Null(SplashTheme.Import(themePath, _targetDir));
        Assert.False(Directory.Exists(_targetDir));
    }

    [Fact]
    public void ADriveRelativeEntryNameIsRejectedAndWritesNothingOutsideTheStagingDirectory()
    {
        // "D:logo.png" carries no separator at all, yet Path.IsPathRooted is true for
        // it: Path.Combine(stagingDirectory, "d:logo.png") returns the second argument
        // VERBATIM, so extracting under the raw entry name lands in that drive's
        // current directory (the test process's own working directory when it sits on
        // the same drive) instead of the staging directory.
        var driveLetter = Path.GetPathRoot(Environment.CurrentDirectory)![..1];
        var entryName = driveLetter + ":logo.png";
        var driveCurrentDirectoryTarget = Path.GetFullPath(
            Path.Combine(_targetDir, entryName.ToLowerInvariant())
        );
        var driveRootTarget = driveLetter + @":\logo.png";
        var driveRootExistedBefore = File.Exists(driveRootTarget);
        var themePath = Path.Combine(_root, "drive-relative.wsgmsplash");
        using (var archive = ZipFile.Open(themePath, ZipArchiveMode.Create))
        {
            WriteJsonEntry(archive, "{}");
            var entry = archive.CreateEntry(entryName);
            using var stream = entry.Open();
            stream.Write([1, 2, 3]);
        }
        // The archive really does carry the hostile name — ZipArchive stores it as-is.
        using (var written = ZipFile.OpenRead(themePath))
        {
            Assert.Contains(written.Entries, e => e.FullName == entryName);
        }

        Assert.Null(SplashTheme.Import(themePath, _targetDir));

        Assert.False(Directory.Exists(_targetDir));
        Assert.False(File.Exists(driveCurrentDirectoryTarget));
        Assert.False(File.Exists(Path.Combine(Environment.CurrentDirectory, "logo.png")));
        Assert.Equal(driveRootExistedBefore, File.Exists(driveRootTarget));
    }

    [Theory]
    [InlineData("D:logo.png", false)]
    [InlineData("C:logo.png", false)]
    [InlineData("/logo.png", false)]
    [InlineData(@"\logo.png", false)]
    [InlineData("sub/logo.png", false)]
    [InlineData(@"..\logo.png", false)]
    [InlineData("logo.png", true)]
    public void OnlyABareFileNameIsAcceptedAsAnImageEntryName(string entryName, bool accepted)
    {
        var themePath = Path.Combine(_root, "entry-name.wsgmsplash");
        using (var archive = ZipFile.Open(themePath, ZipArchiveMode.Create))
        {
            WriteJsonEntry(archive, "{}");
            var entry = archive.CreateEntry(entryName);
            using var stream = entry.Open();
            stream.Write([1, 2, 3]);
        }

        var imported = SplashTheme.Import(themePath, _targetDir);

        if (!accepted)
        {
            Assert.Null(imported);
            Assert.False(Directory.Exists(_targetDir));
            return;
        }

        Assert.NotNull(imported);
        Assert.Equal(Path.Combine(_targetDir, "logo.png"), imported.LogoImagePath);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(imported.LogoImagePath));
    }

    [Fact]
    public void AWellFormedImageEntryExtractsInsideTheStagingDirectory()
    {
        var themePath = Path.Combine(_root, "contained.wsgmsplash");
        using (var archive = ZipFile.Open(themePath, ZipArchiveMode.Create))
        {
            WriteJsonEntry(archive, "{}");
            var logo = archive.CreateEntry("logo.png");
            using var stream = logo.Open();
            stream.Write([4, 2]);
        }

        var imported = SplashTheme.Import(themePath, _targetDir);

        Assert.NotNull(imported);
        var extracted = Path.GetFullPath(imported.LogoImagePath);
        Assert.Equal(Path.GetFullPath(_targetDir), Path.GetDirectoryName(extracted));
        Assert.StartsWith(
            Path.GetFullPath(_targetDir) + Path.DirectorySeparatorChar,
            extracted,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Equal([4, 2], File.ReadAllBytes(extracted));
    }

    [Fact]
    public void MidExtractionFailureCleansUpTheAlreadyExtractedFiles()
    {
        var themePath = Path.Combine(_root, "half-extractable.wsgmsplash");
        using (var archive = ZipFile.Open(themePath, ZipArchiveMode.Create))
        {
            WriteJsonEntry(archive, "{}");
            var logo = archive.CreateEntry("logo.png");
            using (var stream = logo.Open())
            {
                stream.Write([1, 2, 3]);
            }
            var background = archive.CreateEntry("background.png");
            using (var stream = background.Open())
            {
                stream.Write([4, 5, 6]);
            }
        }
        // A directory squatting on background.png's destination makes the second
        // extraction fail after logo.png already landed — the failed import must
        // remove the partial state it created.
        Directory.CreateDirectory(Path.Combine(_targetDir, "background.png"));

        Assert.Null(SplashTheme.Import(themePath, _targetDir));
        Assert.False(File.Exists(Path.Combine(_targetDir, "logo.png")));
    }

    [Fact]
    public void FailedImportIntoAFreshDirectoryDeletesTheDirectoryAgain()
    {
        var themePath = Path.Combine(_root, "unreadable-image.wsgmsplash");
        using (var archive = ZipFile.Open(themePath, ZipArchiveMode.Create))
        {
            WriteJsonEntry(archive, "{}");
            var logo = archive.CreateEntry("logo.png");
            using (var stream = logo.Open())
            {
                stream.Write([1, 2, 3]);
            }
            var background = archive.CreateEntry("background.png");
            using (var stream = background.Open())
            {
                stream.Write([4, 5, 6]);
            }
        }
        // Corrupt background.png's compressed bytes (right after its local file
        // header) so its extraction throws after the fresh target directory was
        // created and logo.png staged.
        var bytes = File.ReadAllBytes(themePath);
        var nameOffset = IndexOf(bytes, "background.png"u8.ToArray());
        var localHeaderOffset = nameOffset - 30;
        var extraFieldLength = bytes[localHeaderOffset + 28] | (bytes[localHeaderOffset + 29] << 8);
        var dataOffset = nameOffset + "background.png".Length + extraFieldLength;
        for (var i = 0; i < 4; i++)
        {
            bytes[dataOffset + i] ^= 0xFF;
        }
        File.WriteAllBytes(themePath, bytes);

        Assert.Null(SplashTheme.Import(themePath, _targetDir));
        Assert.False(Directory.Exists(_targetDir));
    }

    [Fact]
    public void FailedExportOverAnExistingFilePreservesTheOriginalBytesAndLeavesNoTempFile()
    {
        var destination = Path.Combine(_root, "existing.wsgmsplash");
        var originalBytes = new byte[] { 0x50, 0x4B, 1, 2, 3, 4 };
        File.WriteAllBytes(destination, originalBytes);
        var logoPath = Path.Combine(_sourceDir, "locked.png");
        File.WriteAllBytes(logoPath, [9]);

        // An exclusively locked image makes the archive build fail mid-way; the
        // destination must keep its previous content and no *.tmp sibling may remain.
        using (File.Open(logoPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(SplashTheme.Export(new SplashConfig { LogoImagePath = logoPath }, destination));
        }

        Assert.Equal(originalBytes, File.ReadAllBytes(destination));
        Assert.Equal([destination], Directory.GetFiles(_root));
    }

    private static void WriteJsonEntry(ZipArchive archive, string json)
    {
        var entry = archive.CreateEntry("splash.json");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(json);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var start = 0; start <= haystack.Length - needle.Length; start++)
        {
            var match = true;
            for (var i = 0; i < needle.Length; i++)
            {
                if (haystack[start + i] != needle[i])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                return start;
            }
        }
        return -1;
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

    [Fact]
    public void ImportClampsAbsurdSizesAndUnknownEnumsFromASharedTheme()
    {
        // A shared theme is untrusted: the renderer only lower-bounds these values,
        // so an int.MaxValue spinner would blow up layout before the splash is usable.
        var themePath = Path.Combine(_root, "absurd.wsgmsplash");
        using (var archive = ZipFile.Open(themePath, ZipArchiveMode.Create))
        {
            WriteJsonEntry(
                archive,
                """
                { "SpinnerSize": 2147483647, "TitleFontSize": 100000, "CaptionFontSize": 0,
                  "LogoMaxSize": -1, "SpinnerStyle": 999,
                  "TextPlacement": { "Mode": 42, "Anchor": -1, "PaddingX": 999999, "PaddingY": -8, "X": -20000, "Y": 999999 } }
                """
            );
        }

        var imported = SplashTheme.Import(themePath, _targetDir);

        Assert.NotNull(imported);
        Assert.Equal(1024, imported.SpinnerSize);
        Assert.Equal(400, imported.TitleFontSize);
        Assert.Equal(1, imported.CaptionFontSize);
        Assert.Equal(1, imported.LogoMaxSize);
        Assert.Equal(SplashSpinnerStyle.Ring, imported.SpinnerStyle);
        Assert.Equal(SplashPlacementMode.Anchor, imported.TextPlacement.Mode);
        Assert.Equal(SplashPlacementAnchor.Center, imported.TextPlacement.Anchor);
        Assert.Equal(4096, imported.TextPlacement.PaddingX);
        Assert.Equal(0, imported.TextPlacement.PaddingY);
        Assert.Equal(0, imported.TextPlacement.X);
        Assert.Equal(16384, imported.TextPlacement.Y);
    }

    [Fact]
    public void ImportKeepsInRangeValuesFromASharedThemeExactly()
    {
        var themePath = Path.Combine(_root, "in-range.wsgmsplash");
        var original = FullyCustomized(logoPath: "", backgroundPath: "");

        Assert.True(SplashTheme.Export(original, themePath));
        var imported = SplashTheme.Import(themePath, _targetDir);

        Assert.NotNull(imported);
        AssertNonImageFieldsEqual(original, imported);
    }
}
