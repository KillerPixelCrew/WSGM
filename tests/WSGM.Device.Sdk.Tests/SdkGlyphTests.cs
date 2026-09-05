using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Serialization;

namespace WSGM.Device.Tests;

public sealed class SdkGlyphTests
{
    [Fact]
    public void Import_DirectlyEnumeratedHashPinnedProfile_ShipsTheAuthorsOwnBytes()
    {
        byte[] svg = Svg("<path d=\"M 0 0 L 64 0 L 64 64 Z\" fill=\"currentColor\"/>");
        DictionaryGlyphSource source = Source(svg);

        GlyphPackageImportResult result = GlyphPackageImporter.Import(source);

        Assert.True(result.IsValid, Describe(result));
        ImportedGlyphProfile profile = Assert.Single(result.Profiles);
        ImportedGlyphAsset asset = Assert.Single(profile.Assets).Value;

        // Steam is handed exactly what the author wrote. The importer used to re-serialize an
        // allowlisted subset instead, which silently discarded whatever the allowlist had not
        // anticipated — grouping most of all, which is how any real illustration is drawn.
        Assert.Equal(svg, asset.Vector!.SvgUtf8.ToArray());
        Assert.NotEmpty(asset.Vector.Paths);
    }

    [Fact]
    public void Import_GroupedPresentation_IsInheritedRatherThanRefused()
    {
        // The Claw's controller illustration carries its stroke on nine nested groups. Refusing a
        // group with attributes made that artwork unpackageable; dropping the attributes would have
        // drawn it as unstyled outlines.
        byte[] svg = Svg(
            "<g stroke=\"#899099\" stroke-width=\"2\">"
                + "<path d=\"M 0 0 L 64 0\"/>"
                + "</g>");

        GlyphPackageImportResult result = GlyphPackageImporter.Import(Source(svg));

        Assert.True(result.IsValid, Describe(result));
        ImportedGlyphAsset asset = Assert.Single(
            Assert.Single(result.Profiles).Assets).Value;
        NormalizedGlyphPath path = Assert.Single(asset.Vector!.Paths);
        Assert.Equal("#899099", path.Stroke);
        Assert.Equal(2m, path.StrokeWidth);
    }

    [Fact]
    public void Import_MarkupTheRendererCannotDraw_StillReachesSteamIntact()
    {
        // Asset handling is integrity, not defence. A plugin is an assembly WSGM loads and
        // runs — it holds WMI, HID and EC access and can reach Steam's debug port directly — so
        // filtering its artwork constrained nothing while refusing artwork that was simply drawn
        // with more than the allowlist knew. What matters is that the bytes arrive unaltered and
        // that WSGM's own renderer takes only what it understands.
        byte[] svg = Svg(
            "<style>.x{fill:red}</style><path d=\"M 0 0 L 8 8\" fill=\"currentColor\"/>");

        GlyphPackageImportResult result = GlyphPackageImporter.Import(Source(svg));

        Assert.True(result.IsValid, Describe(result));
        ImportedGlyphAsset asset = Assert.Single(
            Assert.Single(result.Profiles).Assets).Value;
        Assert.Equal(svg, asset.Vector!.SvgUtf8.ToArray());
        Assert.Single(asset.Vector.Paths);
    }

    [Fact]
    public void Import_MalformedSvg_IsStillRejected()
    {
        // Integrity is what remains: a corrupt asset fails at import with a reason, rather than
        // reaching Steam and rendering as nothing.
        byte[] svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\"><path"u8
            .ToArray();

        GlyphPackageImportResult result = GlyphPackageImporter.Import(Source(svg));

        Assert.Empty(result.Profiles);
        Assert.Contains(result.Errors, error => error.Code is GlyphPackageImportCode.AssetRejected);
    }

    [Fact]
    public void Import_SvgViewBoxMustMatchItsLockEntry()
    {
        byte[] svg = Svg("<path d=\"M 0 0 L 64 64\"/>");
        DictionaryGlyphSource source = Source(svg, new GlyphViewBox(0, 0, 32, 32));

        GlyphPackageImportResult result = GlyphPackageImporter.Import(source);

        Assert.Empty(result.Profiles);
        Assert.Contains(result.Errors, error => error.Message.Contains(
            "DimensionMismatch",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Import_ValidatesTheWholeSvgAfterThePathProjectionLimit()
    {
        string paths = string.Concat(Enumerable.Repeat("<path d=\"M0 0\"/>", GlyphProfileLimits.MaxSvgPaths));
        byte[] svg = Svg(paths + "<broken");

        GlyphPackageImportResult result = GlyphPackageImporter.Import(Source(svg));

        Assert.Empty(result.Profiles);
        Assert.Contains(result.Errors, error => error.Code is GlyphPackageImportCode.AssetRejected);
    }

    [Fact]
    public void Import_RejectsMorePathCommandsThanTheDeclaredRendererLimit()
    {
        string commands = string.Concat(Enumerable.Repeat("M0 0 ", GlyphProfileLimits.MaxSvgCommands + 1));
        byte[] svg = Svg($"<path d=\"{commands}\"/>");

        GlyphPackageImportResult result = GlyphPackageImporter.Import(Source(svg));

        Assert.Empty(result.Profiles);
        Assert.Contains(result.Errors, error => error.Message.Contains(
            $"more than {GlyphProfileLimits.MaxSvgCommands}",
            StringComparison.Ordinal));
    }

    private static DictionaryGlyphSource Source(
        byte[] svg,
        GlyphViewBox? declaredViewBox = null)
    {
        string hash = Convert.ToHexString(SHA256.HashData(svg)).ToLowerInvariant();
        GlyphProfileManifest manifest = new()
        {
            SchemaVersion = GlyphProfileLimits.CurrentSchemaVersion,
            ProfileId = "synthetic-dock",
            DisplayName = "Synthetic Dock X1",
            Revision = 1,
            ExactDeviceIds = ["synthetic.dock-x1"],
            SourceRevision = "synthetic-revision-1",
            NoticePath = "THIRD_PARTY_NOTICES.md",
            Assets =
            [
                new GlyphAssetLockEntry
                {
                    Sha256 = hash,
                    Format = GlyphAssetFormat.Svg,
                    ByteCount = svg.Length,
                    Role = GlyphAssetRole.Control,
                    ViewBox = declaredViewBox ?? new GlyphViewBox(0, 0, 64, 64),
                },
            ],
            Controls =
            [
                new GlyphControlMapping
                {
                    Control = GlyphControlId.FaceSouth,
                    Presence = GlyphControlPresence.Present,
                    AssetSha256 = hash,
                },
            ],
        };
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            [GlyphPackageLayout.ProfileManifest(manifest.ProfileId)] =
                JsonSerializer.SerializeToUtf8Bytes(
                    manifest,
                    DeviceJsonContext.Default.GlyphProfileManifest),
            [GlyphPackageLayout.Asset(hash, GlyphAssetFormat.Svg)] = svg,
            [manifest.NoticePath] = "Synthetic test artwork.\n"u8.ToArray(),
        };
        return new DictionaryGlyphSource(manifest.ProfileId, files);
    }

    [Fact]
    public void Import_MoreProfilesThanTheLimit_IsReportedRatherThanSilentlyTruncated()
    {
        // A source that cut its enumeration at exactly the limit made this unreachable, so a
        // package carrying more profiles than the format allows validated as conforming with the
        // extras quietly dropped — indistinguishable, in the installed-package diagnostics, from
        // one that never had them.
        string[] identifiers =
        [
            .. Enumerable.Range(0, GlyphProfileLimits.MaxProfiles + 1)
                .Select(index => $"profile-{index:D2}"),
        ];

        GlyphPackageImportResult result = GlyphPackageImporter.Import(
            new EmptyGlyphSource(identifiers));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Message.Contains(
                $"more than {GlyphProfileLimits.MaxProfiles} glyph profiles",
                StringComparison.Ordinal));
    }

    [Fact]
    public void DirectorySource_EnumeratesOnePastTheLimitSoTheImporterCanSeeIt()
    {
        using TemporaryDirectory root = new();
        string profiles = Path.Combine(root.Root, "glyphs", "profiles");
        Directory.CreateDirectory(profiles);
        for (int index = 0; index < GlyphProfileLimits.MaxProfiles + 5; index++)
        {
            File.WriteAllText(Path.Combine(profiles, $"profile-{index:D2}.json"), "{}");
        }

        ImmutableGlyphPackageDirectorySource source = new(root.Root);

        Assert.Equal(GlyphProfileLimits.MaxProfiles + 1, source.EnumerateProfileIds().Count);
    }

    private static byte[] Svg(string content) => Encoding.UTF8.GetBytes(
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\">{content}</svg>");

    private static string Describe(GlyphPackageImportResult result) =>
        string.Join("; ", result.Errors.Select(error => $"{error.Path}: {error.Message}"));

    /// <summary>A source that advertises identifiers and holds no files for any of them.</summary>
    private sealed class EmptyGlyphSource(IReadOnlyList<string> profileIds) : IGlyphPackageSource
    {
        public IReadOnlyList<string> EnumerateProfileIds() => profileIds;

        public bool TryRead(string relativePath, int maximumBytes, out byte[] bytes)
        {
            bytes = [];
            return false;
        }
    }

    private sealed class DictionaryGlyphSource(
        string profileId,
        IReadOnlyDictionary<string, byte[]> files) : IGlyphPackageSource
    {
        public IReadOnlyList<string> EnumerateProfileIds() => [profileId];

        public bool TryRead(string relativePath, int maximumBytes, out byte[] bytes)
        {
            if (files.TryGetValue(relativePath, out byte[]? value)
                && value.Length <= maximumBytes)
            {
                bytes = value.ToArray();
                return true;
            }

            bytes = [];
            return false;
        }
    }
}
