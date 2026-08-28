using System.Security.Cryptography;
using System.Text;
using WSGM.Device.Contracts.Glyphs;
using WSGM.Device.Contracts.Identity;
using WSGM.Device.Contracts.Packaging;
using WSGM.DeviceLab.Core.Packaging;
using WSGM.DeviceLab.Core.Preflight;

namespace WSGM.DeviceLab.Tests;

public sealed class GlyphPackageWorkflowTests
{
    [Fact]
    public void GenerateGlyphs_EmitsDeterministicSafeAssetUnderFixedPackagePath()
    {
        using TestDirectory directory = new();
        TestPackage package = CreateSourcePackage(directory.Source);
        string output = Path.Combine(directory.Root, "generated");

        GlyphPackageGenerationReport report = GlyphPackageGenerationWorkflow.Generate(
            directory.Source,
            output,
            Boundaries(directory.Root));

        Assert.True(report.Valid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
        string relative = GlyphPackageLayout.GeneratedAsset(package.Asset.Sha256, package.Asset.Format);
        string generatedPath = Path.Combine(output, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(generatedPath));
        Assert.Equal(package.GeneratedBytes, File.ReadAllBytes(generatedPath));
        Assert.Equal(Hash(package.GeneratedBytes), report.GeneratedFileHashes[relative]);
        Assert.Equal(package.NoticeHash, Assert.Single(Assert.Single(report.Profiles).NoticeHashes));
    }

    [Fact]
    public void OfflinePackageValidation_RejectsHandEditedGeneratedGlyphOutput()
    {
        using TestDirectory directory = new();
        TestPackage package = CreateSourcePackage(directory.Source);
        string generatedPath = PackagePath(
            directory.Source,
            GlyphPackageLayout.GeneratedAsset(package.Asset.Sha256, package.Asset.Format));
        Directory.CreateDirectory(Path.GetDirectoryName(generatedPath)!);
        File.WriteAllBytes(generatedPath, Encoding.UTF8.GetBytes("hand edited"));

        PluginPackageValidationReport report = PluginPackageWorkflow.ValidateOffline(directory.Source);

        Assert.Contains(report.Issues,
            issue => issue.Code == "glyph-generated-asset-drift"
                && issue.Path.StartsWith("glyphs/generated/", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateGlyphs_RejectsMissingPinnedNoticeWithoutWritingOutput()
    {
        using TestDirectory directory = new();
        TestPackage package = CreateSourcePackage(directory.Source);
        File.Delete(PackagePath(directory.Source, GlyphPackageLayout.Notice(package.NoticeHash)));
        string output = Path.Combine(directory.Root, "generated");

        GlyphPackageGenerationReport report = GlyphPackageGenerationWorkflow.Generate(
            directory.Source,
            output,
            Boundaries(directory.Root));

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code.Contains("NoticeRejected", StringComparison.Ordinal));
        Assert.False(Directory.Exists(output));
    }

    private static TestPackage CreateSourcePackage(string root)
    {
        Directory.CreateDirectory(root);
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
        GlyphProfileImportResult imported = GlyphProfileImporter.Import(
            profile,
            new AssetSource(asset.Sha256, svg));
        ImportedGlyphProfile generated = Assert.IsType<ImportedGlyphProfile>(imported.Profile);
        byte[] generatedBytes = generated.Assets[asset.Sha256].Vector!.CanonicalSvgUtf8.ToArray();
        byte[] profileBytes = GlyphProfileReader.ToCanonicalUtf8(generated.Manifest);
        GlyphProfilePackageReference reference = new()
        {
            ProfileId = generated.Manifest.ProfileId,
            ManifestSha256 = Hash(profileBytes),
        };
        PluginManifest manifest = new()
        {
            SchemaVersion = 2,
            Id = "wsgm.device.example",
            Version = "1.0.0",
            DisplayName = "Example",
            Publisher = "Example",
            MinApiVersion = 1,
            MaxApiVersion = 1,
            EntryPoint = "Example.dll",
            GlyphProfiles = [reference],
            Devices =
            [
                new DeviceDefinition
                {
                    Id = "example-device",
                    DisplayName = "Example device",
                    GlyphProfileId = reference.ProfileId,
                    Identity =
                    [
                        new IdentityObservation
                        {
                            Signal = IdentitySignal.SmbiosBaseboardProduct,
                            Strength = IdentityStrength.Required,
                            Values = ["EXAMPLE"],
                        },
                    ],
                },
            ],
            Provenance = new PackageProvenance
            {
                Source = "Tests",
                License = "MIT",
                ProvenanceClass = ProvenanceClass.IndependentCapture,
            },
        };

        Write(root, PluginPackageWorkflow.ManifestPath, PluginManifestReader.ToCanonicalUtf8(manifest));
        Write(root, GlyphPackageLayout.ProfileManifest(reference.ManifestSha256), profileBytes);
        Write(root, GlyphPackageLayout.Asset(asset.Sha256, asset.Format), svg);
        Write(root, GlyphPackageLayout.Notice(noticeHash), notice);
        return new TestPackage(asset, noticeHash, generatedBytes);
    }

    private static void Write(string root, string relative, byte[] bytes)
    {
        string path = PackagePath(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static string PackagePath(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static DeviceLabPathBoundaries Boundaries(string root) => new()
    {
        LiveDataDirectory = Path.Combine(root, "never-live"),
        BroadHomeDirectories = [],
    };

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record TestPackage(
        GlyphAssetLockEntry Asset,
        string NoticeHash,
        byte[] GeneratedBytes);

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

    private sealed class TestDirectory : IDisposable
    {
        internal TestDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), $"wsgm-glyph-lab-{Guid.NewGuid():N}");
            Source = Path.Combine(Root, "source");
        }

        internal string Root { get; }

        internal string Source { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
