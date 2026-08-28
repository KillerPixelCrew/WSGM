using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using WSGM.Device.Contracts.Glyphs;

namespace WSGM.Device.Contracts.Tests;

public sealed class GlyphProfileTests
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";
    private const string NoticeHash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void CanonicalProfile_IsByteIdenticalAcrossSetOrdering()
    {
        byte[] svgA = Svg("M 0 0 L 8 8 Z");
        byte[] svgB = Svg("M 8 0 L 0 8 Z");
        GlyphAssetLockEntry first = Asset(svgA);
        GlyphAssetLockEntry second = Asset(svgB);
        GlyphProfileManifest left = Profile([first, second]) with
        {
            ExactDeviceIds = ["device-z", "device-a"],
            Controls =
            [
                Control(GlyphControlId.FaceEast, second.Sha256),
                Control(GlyphControlId.FaceSouth, first.Sha256),
            ],
        };
        GlyphProfileManifest right = left with
        {
            ExactDeviceIds = ["device-a", "device-z"],
            Assets = [second, first],
            Controls = left.Controls.Reverse().ToArray(),
        };

        Assert.Equal(
            GlyphProfileReader.ToCanonicalUtf8(left),
            GlyphProfileReader.ToCanonicalUtf8(right));
    }

    [Fact]
    public void ExactVerifiedProfile_RequiresAnExactDeviceIdentity()
    {
        GlyphProfileManifest profile = Profile([]) with { ExactDeviceIds = [] };

        Assert.Contains(
            GlyphProfileValidator.Validate(profile),
            error => error.Code == GlyphProfileValidationCode.MissingVerificationTarget);
    }

    [Fact]
    public void ProfileSchema_HasNoPathOrUrlSurface()
    {
        string json = Encoding.UTF8.GetString(GlyphProfileReader.ToCanonicalUtf8(Profile([])));

        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageLayout_IsDerivedOnlyFromCanonicalContentHash()
    {
        string hash = new('a', 64);

        Assert.Equal($"glyphs/profiles/{hash}.json", GlyphPackageLayout.ProfileManifest(hash));
        Assert.Equal($"glyphs/assets/{hash}.svg", GlyphPackageLayout.Asset(hash, GlyphAssetFormat.Svg));
        Assert.Equal($"glyphs/notices/{hash}.txt", GlyphPackageLayout.Notice(hash));
        Assert.Throws<ArgumentException>(() => GlyphPackageLayout.ProfileManifest("../escape"));
        Assert.Throws<ArgumentException>(() => GlyphPackageLayout.Asset(new string('A', 64), GlyphAssetFormat.Png));
    }

    [Fact]
    public void Normalizer_ReemitsOwnCanonicalSvgModel()
    {
        byte[] svg = Encoding.UTF8.GetBytes(
            $"<svg viewBox=\"0,0,64,64\" xmlns=\"{SvgNamespace}\">\n"
            + "  <path stroke-linecap=\"ROUND\" fill=\"#FFFFFF\" d=\"M0,0L64,64z\"/>\n"
            + "</svg>");
        GlyphAssetLockEntry asset = Asset(svg);

        GlyphProfileImportResult result = GlyphProfileImporter.Import(
            Profile([asset]),
            new MemorySource(asset.Sha256, svg));

        Assert.True(result.IsValid, Describe(result));
        byte[] normalized = result.Profile!.Assets[asset.Sha256].Vector!.CanonicalSvgUtf8.ToArray();
        Assert.NotEqual(svg, normalized);
        Assert.DoesNotContain("\n", Encoding.UTF8.GetString(normalized), StringComparison.Ordinal);
        Assert.Contains("stroke-linecap=\"round\"", Encoding.UTF8.GetString(normalized));

        GlyphProfileImportResult repeated = GlyphProfileImporter.Import(
            Profile([asset]),
            new MemorySource(asset.Sha256, svg));
        Assert.Equal(normalized,
            repeated.Profile!.Assets[asset.Sha256].Vector!.CanonicalSvgUtf8.ToArray());
    }

    [Theory]
    [InlineData("<script/>")]
    [InlineData("<style>path{fill:red}</style>")]
    [InlineData("<foreignObject/>")]
    [InlineData("<image href=\"https://example.invalid/x.png\"/>")]
    [InlineData("<use href=\"#other\"/>")]
    [InlineData("<text>secret</text>")]
    public void Normalizer_RejectsActiveAndReferenceElements(string content)
    {
        AssertUnsafeSvg($"<svg xmlns=\"{SvgNamespace}\" viewBox=\"0 0 64 64\">{content}</svg>");
    }

    [Theory]
    [InlineData("onclick", "doSomething()")]
    [InlineData("style", "fill:url(https://example.invalid/a)")]
    [InlineData("transform", "translate(1 1)")]
    [InlineData("filter", "url(#blur)")]
    [InlineData("href", "data:image/png;base64,AA==")]
    public void Normalizer_RejectsActiveExternalAndUnsupportedPathAttributes(
        string attribute,
        string value)
    {
        AssertUnsafeSvg(
            $"<svg xmlns=\"{SvgNamespace}\" viewBox=\"0 0 64 64\">"
            + $"<path d=\"M 0 0 L 1 1\" {attribute}=\"{value}\"/></svg>");
    }

    [Fact]
    public void Normalizer_RejectsEntityDeclarationsBeforeExpansion()
    {
        AssertUnsafeSvg(
            $"<!DOCTYPE svg [<!ENTITY payload SYSTEM \"file:///secret\">]>"
            + $"<svg xmlns=\"{SvgNamespace}\" viewBox=\"0 0 64 64\">"
            + "<path d=\"M 0 0 L 1 1\" fill=\"&payload;\"/></svg>");
    }

    [Theory]
    [InlineData("M 0")]
    [InlineData("L 0 0")]
    [InlineData("M 0 0 X 1 1")]
    [InlineData("M 0 0 A 1 1 0 2 0 3 3")]
    [InlineData("M NaN 0")]
    public void Normalizer_RejectsMalformedPathGeometry(string path)
    {
        byte[] svg = Svg(path);
        GlyphAssetLockEntry asset = Asset(svg);

        GlyphProfileImportResult result = GlyphProfileImporter.Import(
            Profile([asset]),
            new MemorySource(asset.Sha256, svg));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == GlyphAssetImportCode.UnsafeGeometry);
    }

    [Fact]
    public void Import_RejectsHashMismatchBeforeParsingTrustedOutput()
    {
        byte[] svg = Svg("M 0 0 L 8 8");
        GlyphAssetLockEntry asset = Asset(svg) with { Sha256 = new string('0', 64) };

        GlyphProfileImportResult result = GlyphProfileImporter.Import(
            Profile([asset]),
            new MemorySource(asset.Sha256, svg));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == GlyphAssetImportCode.HashMismatch);
    }

    [Fact]
    public void Import_RejectsLockedViewBoxMismatch()
    {
        byte[] svg = Svg("M 0 0 L 8 8");
        GlyphAssetLockEntry asset = Asset(svg) with
        {
            ViewBox = new GlyphViewBox(0, 0, 32, 32),
        };

        GlyphProfileImportResult result = GlyphProfileImporter.Import(
            Profile([asset]),
            new MemorySource(asset.Sha256, svg));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors,
            error => error.Code == GlyphAssetImportCode.DimensionMismatch);
    }

    [Fact]
    public void PngImporter_ValidatesBoundedStaticDecodeAndCrc()
    {
        byte[] png = OnePixelPng();
        GlyphAssetLockEntry asset = PngAsset(png);

        GlyphProfileImportResult result = GlyphProfileImporter.Import(
            Profile([asset]),
            new MemorySource(asset.Sha256, png));

        Assert.True(result.IsValid, Describe(result));
        Assert.Equal(png, result.Profile!.Assets[asset.Sha256].RasterPng.ToArray());

        byte[] corrupt = png.ToArray();
        corrupt[^1] ^= 0xff;
        GlyphAssetLockEntry corruptAsset = PngAsset(corrupt);
        GlyphProfileImportResult rejected = GlyphProfileImporter.Import(
            Profile([corruptAsset]),
            new MemorySource(corruptAsset.Sha256, corrupt));
        Assert.Contains(rejected.Errors,
            error => error.Code == GlyphAssetImportCode.MalformedAsset);
    }

    [Fact]
    public void AbsentControl_CannotAcquireCapabilityFromArtwork()
    {
        byte[] svg = Svg("M 0 0 L 8 8");
        GlyphAssetLockEntry asset = Asset(svg);
        GlyphProfileManifest profile = Profile([asset]) with
        {
            Controls =
            [
                new GlyphControlMapping
                {
                    Control = GlyphControlId.LeftTrackpad,
                    Presence = GlyphControlPresence.Absent,
                    AssetSha256 = asset.Sha256,
                },
            ],
        };

        Assert.Contains(
            GlyphProfileValidator.Validate(profile),
            error => error.Code == GlyphProfileValidationCode.InvalidControl);
    }

    private static void AssertUnsafeSvg(string source)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        GlyphAssetLockEntry asset = Asset(bytes);
        GlyphProfileImportResult result = GlyphProfileImporter.Import(
            Profile([asset]),
            new MemorySource(asset.Sha256, bytes));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors,
            error => error.Code is GlyphAssetImportCode.UnsafeSvg
                or GlyphAssetImportCode.UnsafeGeometry);
    }

    private static byte[] Svg(string path) => Encoding.UTF8.GetBytes(
        $"<svg xmlns=\"{SvgNamespace}\" viewBox=\"0 0 64 64\">"
        + $"<path d=\"{path}\"/></svg>");

    private static GlyphAssetLockEntry Asset(byte[] bytes) => new()
    {
        Sha256 = Hash(bytes),
        Format = GlyphAssetFormat.Svg,
        ByteCount = bytes.Length,
        Role = GlyphAssetRole.Control,
        ViewBox = new GlyphViewBox(0, 0, 64, 64),
        Conversion = GlyphConversionKind.NormalizedVector,
        ImporterVersion = GlyphProfileImporter.CurrentImporterVersion,
        Provenance = Provenance(),
    };

    private static GlyphAssetLockEntry PngAsset(byte[] bytes) => new()
    {
        Sha256 = Hash(bytes),
        Format = GlyphAssetFormat.Png,
        ByteCount = bytes.Length,
        Role = GlyphAssetRole.Control,
        PixelWidth = 1,
        PixelHeight = 1,
        Conversion = GlyphConversionKind.ReviewedRaster,
        ImporterVersion = GlyphProfileImporter.CurrentImporterVersion,
        Provenance = Provenance(),
    };

    private static GlyphProfileManifest Profile(IReadOnlyList<GlyphAssetLockEntry> assets) => new()
    {
        SchemaVersion = GlyphProfileLimits.CurrentSchemaVersion,
        ProfileId = "example.handheld",
        DisplayName = "Example handheld",
        Revision = 1,
        Verification = GlyphProfileVerification.ExactDeviceVerified,
        ExactDeviceIds = ["example-device"],
        Provenance = Provenance(),
        Assets = assets,
        Controls = [],
    };

    private static GlyphControlMapping Control(GlyphControlId control, string hash) => new()
    {
        Control = control,
        Presence = GlyphControlPresence.Present,
        AssetSha256 = hash,
    };

    private static GlyphProfileProvenance Provenance() => new()
    {
        SourceId = "example.source",
        SourceRevision = "revision-1",
        License = "MIT",
        LicenseNoticeSha256 = NoticeHash,
    };

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] OnePixelPng()
    {
        using MemoryStream output = new();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 1);
        header[8] = 8;
        header[9] = 6;
        WritePngChunk(output, "IHDR", header);

        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write([0, 255, 0, 0, 255]);
        }
        WritePngChunk(output, "IDAT", compressed.ToArray());
        WritePngChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WritePngChunk(Stream output, string type, byte[] data)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        byte[] crcInput = [.. typeBytes, .. data];
        byte[] crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
        output.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                uint mask = 0u - (crc & 1u);
                crc = (crc >> 1) ^ (0xedb88320u & mask);
            }
        }
        return ~crc;
    }

    private static string Describe(GlyphProfileImportResult result) =>
        string.Join("; ", result.Errors.Select(error => $"{error.Code}: {error.Message}"));

    private sealed class MemorySource(string hash, byte[] bytes) : IGlyphAssetSource
    {
        public bool TryRead(string sha256, int maximumBytes, out byte[] result)
        {
            if (sha256 == hash && bytes.Length <= maximumBytes)
            {
                result = bytes.ToArray();
                return true;
            }
            result = [];
            return false;
        }
    }
}
