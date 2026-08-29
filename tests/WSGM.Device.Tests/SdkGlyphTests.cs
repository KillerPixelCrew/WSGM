using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Ipc;

namespace WSGM.Device.Tests;

public sealed class SdkGlyphTests
{
    [Fact]
    public void Import_DirectlyEnumeratedHashPinnedProfile_ReturnsNormalizedArtwork()
    {
        byte[] svg = Svg("<path d=\"M 0 0 L 64 0 L 64 64 Z\" fill=\"currentColor\"/>");
        DictionaryGlyphSource source = Source(svg);

        GlyphPackageImportResult result = GlyphPackageImporter.Import(source);

        Assert.True(result.IsValid, Describe(result));
        ImportedGlyphProfile profile = Assert.Single(result.Profiles);
        ImportedGlyphAsset asset = Assert.Single(profile.Assets).Value;
        Assert.NotEmpty(asset.Vector!.Paths);
        Assert.NotEqual(svg, asset.Vector.CanonicalSvgUtf8.ToArray());
    }

    [Fact]
    public void Import_ActiveSvgMarkup_IsRejectedWithoutReturningAPartialProfile()
    {
        byte[] svg = Svg(
            "<script>throw new Error('active')</script>"
                + "<path d=\"M 0 0 L 8 8\" fill=\"currentColor\"/>");

        GlyphPackageImportResult result = GlyphPackageImporter.Import(Source(svg));

        Assert.Empty(result.Profiles);
        Assert.Contains(result.Errors, error => error.Code is GlyphPackageImportCode.AssetRejected);
    }

    private static DictionaryGlyphSource Source(byte[] svg)
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
                    ViewBox = new GlyphViewBox(0, 0, 64, 64),
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
                    DeviceWireJsonContext.Default.GlyphProfileManifest),
            [GlyphPackageLayout.Asset(hash, GlyphAssetFormat.Svg)] = svg,
            [manifest.NoticePath] = "Synthetic test artwork.\n"u8.ToArray(),
        };
        return new DictionaryGlyphSource(manifest.ProfileId, files);
    }

    private static byte[] Svg(string content) => Encoding.UTF8.GetBytes(
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\">{content}</svg>");

    private static string Describe(GlyphPackageImportResult result) =>
        string.Join("; ", result.Errors.Select(error => $"{error.Path}: {error.Message}"));

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
