using System.Security.Cryptography;
using System.Text;
using WSGM.Device.Contracts.Glyphs;
using WSGM.Device.Sdk.Authoring;

namespace WSGM.Device.Sdk.Generators.Tests;

public sealed class GlyphProfileBuilderTests
{
    private const string NoticeHash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Builder_ProducesDeterministicHashAddressedPackageInputs()
    {
        byte[] svg = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\">"
            + "<path d=\"M 0 0 L 64 64 Z\"/></svg>");
        string expectedHash = Convert.ToHexString(SHA256.HashData(svg)).ToLowerInvariant();
        GlyphProfileBuilder builder = new(
            "example.handheld",
            "Example handheld",
            1,
            Provenance());

        string hash = builder.AddAsset(
            svg,
            GlyphAssetFormat.Svg,
            GlyphAssetRole.Control,
            Provenance(),
            viewBox: new GlyphViewBox(0, 0, 64, 64));
        AuthoredGlyphProfile result = builder
            .AddControl(
                GlyphControlId.FaceSouth,
                GlyphControlPresence.Present,
                assetSha256: hash)
            .MarkExactDeviceVerified("example-device")
            .Build();

        Assert.Equal(expectedHash, hash);
        Assert.Equal(svg, result.Assets[hash]);
        Assert.Equal(
            GlyphProfileReader.ToCanonicalUtf8(result.Manifest),
            result.CanonicalManifestUtf8);
        Assert.NotEqual(
            svg,
            result.GeneratedAssets[hash].Vector!.CanonicalSvgUtf8.ToArray());
    }

    [Fact]
    public void Builder_RejectsUnsafeSvgBeforeProducingPackageOutput()
    {
        byte[] svg = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\">"
            + "<script/></svg>");
        GlyphProfileBuilder builder = new(
            "example.handheld",
            "Example handheld",
            1,
            Provenance());
        string hash = builder.AddAsset(
            svg,
            GlyphAssetFormat.Svg,
            GlyphAssetRole.Control,
            Provenance(),
            viewBox: new GlyphViewBox(0, 0, 64, 64));
        builder.AddControl(
            GlyphControlId.FaceSouth,
            GlyphControlPresence.Present,
            assetSha256: hash);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => builder.Build());

        Assert.Contains(nameof(GlyphAssetImportCode.UnsafeSvg), error.Message);
    }

    [Fact]
    public void EmptyScaffold_RemainsUnverifiedAndCannotClaimAnExactDevice()
    {
        GlyphProfileBuilder builder = new(
            "example.handheld",
            "Example handheld",
            1,
            Provenance());

        AuthoredGlyphProfile result = builder.Build();

        Assert.Equal(GlyphProfileVerification.Unverified, result.Manifest.Verification);
        Assert.Empty(result.Manifest.ExactDeviceIds);
        Assert.Empty(result.Manifest.Assets);
    }

    private static GlyphProfileProvenance Provenance() => new()
    {
        SourceId = "example.source",
        SourceRevision = "revision-1",
        License = "MIT",
        LicenseNoticeSha256 = NoticeHash,
    };
}
