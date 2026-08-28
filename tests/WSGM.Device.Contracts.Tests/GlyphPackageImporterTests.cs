using System.Security.Cryptography;
using System.Text;
using WSGM.Device.Contracts.Glyphs;
using WSGM.Device.Contracts.Packaging;

namespace WSGM.Device.Contracts.Tests;

public sealed class GlyphPackageImporterTests
{
    [Fact]
    public void Import_VerifiesProfileManifestHashBeforeReadingAnyAsset()
    {
        TestPackage package = CreatePackage();
        string path = GlyphPackageLayout.ProfileManifest(package.Reference.ManifestSha256);
        package.Files[path][0] ^= 1;

        GlyphPackageImportResult result = GlyphPackageImporter.Import(
            package.PackageManifest,
            package.Source);

        Assert.Empty(result.Profiles);
        Assert.Contains(result.Errors,
            error => error.Code == GlyphPackageImportCode.ProfileManifestHashMismatch);
        Assert.Equal(new[] { path }, package.Source.Requests);
    }

    [Fact]
    public void Import_AcceptsOnlyExactGeneratedOutputAndPinnedPlainNotice()
    {
        TestPackage package = CreatePackage();

        GlyphPackageImportResult result = GlyphPackageImporter.Import(
            package.PackageManifest,
            package.Source);

        Assert.True(result.IsValid, Describe(result));
        ImportedGlyphProfile profile = Assert.Single(result.Profiles);
        Assert.Equal("example.handheld", profile.Manifest.ProfileId);
    }

    [Fact]
    public void Import_RejectsGeneratedOutputDriftWithoutReturningPartialProfile()
    {
        TestPackage package = CreatePackage();
        string path = GlyphPackageLayout.GeneratedAsset(package.Asset.Sha256, package.Asset.Format);
        package.Files[path] = Encoding.UTF8.GetBytes("not generated SVG");

        GlyphPackageImportResult result = GlyphPackageImporter.Import(
            package.PackageManifest,
            package.Source);

        Assert.Empty(result.Profiles);
        Assert.Contains(result.Errors,
            error => error.Code == GlyphPackageImportCode.GeneratedAssetDrift);
    }

    [Fact]
    public void Import_RejectsChangedNoticeEvenWhenArtworkIsValid()
    {
        TestPackage package = CreatePackage();
        package.Files[GlyphPackageLayout.Notice(package.NoticeHash)] = Encoding.UTF8.GetBytes("changed");

        GlyphPackageImportResult result = GlyphPackageImporter.Import(
            package.PackageManifest,
            package.Source);

        Assert.Empty(result.Profiles);
        Assert.Contains(result.Errors,
            error => error.Code == GlyphPackageImportCode.NoticeRejected);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:/outside")]
    public void ImmutableDirectorySource_NeverReadsAPathOutsideItsPackage(string relativePath)
    {
        string root = Path.Combine(Path.GetTempPath(), $"wsgm-glyph-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            ImmutableGlyphPackageDirectorySource source = new(root);
            Assert.False(source.TryRead(relativePath, 1024, out byte[] bytes));
            Assert.Empty(bytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static TestPackage CreatePackage()
    {
        byte[] notice = Encoding.UTF8.GetBytes("Example glyph notice\n");
        string noticeHash = Hash(notice);
        GlyphProfileProvenance provenance = new()
        {
            SourceId = "example.source",
            SourceRevision = "0123456789abcdef",
            License = "MIT",
            LicenseNoticeSha256 = noticeHash,
        };
        byte[] svg = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\">"
                + "<path d=\"M 0 0 L 64 0 L 64 64 Z\" fill=\"currentColor\"/>"
                + "</svg>");
        GlyphAssetLockEntry asset = new()
        {
            Sha256 = Hash(svg),
            Format = GlyphAssetFormat.Svg,
            ByteCount = svg.Length,
            Role = GlyphAssetRole.Control,
            ViewBox = new GlyphViewBox(0, 0, 64, 64),
            Conversion = GlyphConversionKind.NormalizedVector,
            ImporterVersion = GlyphProfileImporter.CurrentImporterVersion,
            Provenance = provenance,
        };
        GlyphProfileManifest profile = new()
        {
            SchemaVersion = GlyphProfileLimits.CurrentSchemaVersion,
            ProfileId = "example.handheld",
            DisplayName = "Example handheld",
            Revision = 1,
            Verification = GlyphProfileVerification.ExactDeviceVerified,
            ExactDeviceIds = ["example-device"],
            Provenance = provenance,
            Assets = [asset],
            Controls =
            [
                new GlyphControlMapping
                {
                    Control = GlyphControlId.FaceSouth,
                    Presence = GlyphControlPresence.Present,
                    AssetSha256 = asset.Sha256,
                },
            ],
        };
        GlyphProfileImportResult generated = GlyphProfileImporter.Import(
            profile,
            new AssetSource(asset.Sha256, svg));
        ImportedGlyphProfile imported = Assert.IsType<ImportedGlyphProfile>(generated.Profile);
        byte[] profileBytes = GlyphProfileReader.ToCanonicalUtf8(imported.Manifest);
        GlyphProfilePackageReference reference = new()
        {
            ProfileId = imported.Manifest.ProfileId,
            ManifestSha256 = Hash(profileBytes),
        };
        PluginManifest packageManifest = new()
        {
            SchemaVersion = 2,
            Id = "wsgm.device.example",
            Version = "1.0.0",
            DisplayName = "Example",
            Publisher = "Example",
            MinApiVersion = 1,
            MaxApiVersion = 1,
            EntryPoint = "Example.dll",
            Devices = [],
            GlyphProfiles = [reference],
            Provenance = new PackageProvenance
            {
                Source = "Tests",
                License = "MIT",
                ProvenanceClass = ProvenanceClass.IndependentCapture,
            },
        };
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            [GlyphPackageLayout.ProfileManifest(reference.ManifestSha256)] = profileBytes,
            [GlyphPackageLayout.Asset(asset.Sha256, asset.Format)] = svg,
            [GlyphPackageLayout.GeneratedAsset(asset.Sha256, asset.Format)] =
                imported.Assets[asset.Sha256].Vector!.CanonicalSvgUtf8.ToArray(),
            [GlyphPackageLayout.Notice(noticeHash)] = notice,
        };
        DictionaryPackageSource source = new(files);
        return new TestPackage(packageManifest, reference, asset, noticeHash, files, source);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Describe(GlyphPackageImportResult result) =>
        string.Join("; ", result.Errors.Select(error => $"{error.Code}: {error.Message}"));

    private sealed record TestPackage(
        PluginManifest PackageManifest,
        GlyphProfilePackageReference Reference,
        GlyphAssetLockEntry Asset,
        string NoticeHash,
        Dictionary<string, byte[]> Files,
        DictionaryPackageSource Source);

    private sealed class DictionaryPackageSource(Dictionary<string, byte[]> files) : IGlyphPackageSource
    {
        public List<string> Requests { get; } = [];

        public bool TryRead(string relativePath, int maximumBytes, out byte[] bytes)
        {
            Requests.Add(relativePath);
            if (files.TryGetValue(relativePath, out byte[]? stored) && stored.Length <= maximumBytes)
            {
                bytes = stored.ToArray();
                return true;
            }
            bytes = [];
            return false;
        }
    }

    private sealed class AssetSource(string hash, byte[] asset) : IGlyphAssetSource
    {
        public bool TryRead(string sha256, int maximumBytes, out byte[] bytes)
        {
            if (sha256 == hash && asset.Length <= maximumBytes)
            {
                bytes = asset.ToArray();
                return true;
            }
            bytes = [];
            return false;
        }
    }
}
