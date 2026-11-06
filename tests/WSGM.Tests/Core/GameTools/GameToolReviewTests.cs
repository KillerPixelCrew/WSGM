using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using WSGM.Core;
using WSGM.Testing;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Core;

public sealed class GameToolReviewTests
{
    [Theory]
    [InlineData("reshade")]
    [InlineData("special-k")]
    public void MixedDirect3DImportsMatchCombinedModesAndOccupiedNamesAreExcluded(string id)
    {
        var tool = GameToolCatalog.Current.Find(id)!;
        var free = tool.ProxyOptions.Select(option =>
            new GameToolProxyStatus(option.Name, option.Hint, true, null, option.Suggest)).ToArray();
        Assert.Equal("dxgi.dll", GameToolInstaller.SuggestProxy(free, tool.Proxy.Default, tool,
            ["Direct3D 9", "Direct3D 11"], "x64"));
        var occupied = free.Select(option => option.Name == "dxgi.dll"
            ? option with { Available = false, Conflict = "Game file" } : option).ToArray();
        Assert.Equal("d3d9.dll", GameToolInstaller.SuggestProxy(occupied, tool.Proxy.Default, tool,
            ["Direct3D 9", "Direct3D 11"], "x64"));
        Assert.Equal("", GameToolInstaller.SuggestProxy(occupied, tool.Proxy.Default, tool, ["Direct3D 11"], "x64"));
        Assert.All(tool.ProxyOptions, option => Assert.Contains(tool.InstallModes, mode => mode.Proxy == option.Name));
    }

    [Fact]
    public void WebsiteSelectsNewestVersionAndSourceNormalizesItsOwnTag()
    {
        var source = GameToolCatalog.Current.Find("reshade")!.DefaultChannel.Source;
        var release = GameToolReleases.WebsiteRelease(source,
            "ReShade_Setup_6.8.0.exe ReShade_Setup_6.10.0.exe ReShade_Setup_6.9.1.exe");
        Assert.Equal("6.10.0", release.Version);
        Assert.Equal("https://reshade.me/downloads/ReShade_Setup_6.10.0.exe", release.Asset.Url);
        var channel = GameToolCatalog.Current.Find("special-k")!.DefaultChannel;
        Assert.Equal("26.10.10", channel.Normalize(release with { Version = "SK_26_10_10" }).Version);
    }

    [Fact]
    public void EnabledIsAnActionAndCannotBeSmuggledThroughInstallSettings()
    {
        var tool = GameToolCatalog.Current.Find("dxvk")!;
        Assert.NotNull(GameToolAdmission.Settings(tool, new Dictionary<string, string> { ["enabled"] = "off" }));
        var manifest = new GameToolManifest { Enabled = false, State = GameToolManifestState.Installed };
        var inspection = new GameToolInspection([], [], [], []);
        var state = GameToolStatus.Evaluate(tool, "ready", null, "x64", manifest, inspection, null,
            tool.DefaultChannel, []);
        Assert.Contains("enable", state.Actions);
        Assert.DoesNotContain("repair", state.Actions);
        Assert.DoesNotContain("configure", state.Actions);
    }

    [Fact]
    public void ProductIdentityDoesNotIdentifyCompanionLibrariesAsTheInjector()
    {
        var tool = GameToolCatalog.Current.Find("dxvk")!;
        Assert.False(GameToolIdentityCheck.IsTool(tool,
            new PeVersionInfo("dxvk-nvapi.dll", "", "", "DXVK companion", "dxvk-nvapi", "", "DXVK")));
        Assert.Equal("Direct3D 11", GameGraphicsApi.Classify("D3D11.DLL"u8));
        Assert.Null(GameGraphicsApi.Classify("d3d11.dll.extra"u8));
    }

    [Fact]
    public void TarMetadataIsIgnoredButLinksAndEscapingPathsAreRefused()
    {
        using var directory = new TemporaryDirectory();
        var archive = Path.Combine(directory.Root, "tool.tar.gz");
        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionMode.Compress))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            writer.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(
                new Dictionary<string, string> { ["comment"] = "publisher metadata" }));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "x64/d3d11.dll")
            {
                DataStream = new MemoryStream("payload"u8.ToArray())
            });
        }

        var output = Path.Combine(directory.Root, "good");
        PackageArchive.Extract(archive, output, CancellationToken.None);
        Assert.Equal("payload", File.ReadAllText(Path.Combine(output, "x64", "d3d11.dll")));
        foreach (var type in new[] { TarEntryType.SymbolicLink, TarEntryType.HardLink, TarEntryType.RegularFile })
        {
            using (var file = File.Create(archive))
            using (var gzip = new GZipStream(file, CompressionMode.Compress))
            using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
            {
                var entry = new PaxTarEntry(type, type == TarEntryType.RegularFile ? "../outside.dll" : "link.dll");
                if (type == TarEntryType.RegularFile)
                {
                    entry.DataStream = new MemoryStream([1]);
                }
                else
                {
                    entry.LinkName = "target.dll";
                }

                writer.WriteEntry(entry);
            }

            Assert.Throws<InvalidDataException>(() => PackageArchive.Extract(archive,
                Path.Combine(directory.Root, Guid.NewGuid().ToString("N")), CancellationToken.None));
        }

        Assert.False(File.Exists(Path.Combine(directory.Root, "outside.dll")));
    }

    [Fact]
    public void StreamExtractionRejectsLinksAndObservesCancellation()
    {
        using var directory = new TemporaryDirectory();
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("link.dll");
            entry.ExternalAttributes = unchecked((int)(0xa1ffu << 16));
            using var output = entry.Open();
            output.Write("target.dll"u8);
        }

        bytes.Position = 0;
        Assert.Throws<InvalidDataException>(() => PackageArchive.Extract(bytes,
            Path.Combine(directory.Root, "links"), CancellationToken.None));
        using var cancelledBytes = new MemoryStream(bytes.ToArray());
        Assert.Throws<OperationCanceledException>(() => PackageArchive.Extract(cancelledBytes,
            Path.Combine(directory.Root, "cancelled"), new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task SignatureVerifiedWebsitePackagesReuseCacheAndEnforceDownloadLimits()
    {
        using var directory = new TemporaryDirectory();
        var (bytes, thumbprint) = SignedSetup();
        var bundled = GameToolCatalog.Current.Find("reshade")!;
        var source = bundled.DefaultChannel.Source with { SignerThumbprint = thumbprint };
        var channel = bundled.DefaultChannel with { Source = source };
        var tool = bundled with { Channels = [channel], Identity = new GameToolIdentity() };
        var release = new HostedRelease("1.0", "1.0", "", "Website",
            new ReleaseAsset("Setup_1.0.exe", "https://example.test/Setup_1.0.exe"));
        var calls = 0;
        using var handler = new StubHttpMessageHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        using var releases = new GameToolReleases(directory.Root, handler);
        var first = await releases.PrepareAsync(tool, channel, release, (_, _, _) => { },
            CancellationToken.None, "x64", "dxgi.dll");
        var cached = await releases.PrepareAsync(tool, channel, release, (_, _, _) => { },
            CancellationToken.None, "x64", "dxgi.dll");
        Assert.Equal(first.Root, cached.Root);
        Assert.NotEmpty(cached.Release.Asset.Sha256);
        Assert.Equal(1, calls);
        var limited = channel with { Source = source with { MaximumDownloadBytes = 16 } };
        await Assert.ThrowsAsync<GameToolException>(() => releases.PrepareAsync(tool, limited,
            release with { Version = "2.0" }, (_, _, _) => { }, CancellationToken.None, "x64", "dxgi.dll"));
        Assert.Equal(2, calls);
        await releases.PrepareAsync(tool, limited,
            release with { Version = "3.0", Asset = release.Asset with { Size = bytes.Length } },
            (_, _, _) => { }, CancellationToken.None, "x64", "dxgi.dll");
        Assert.Equal(3, calls);
        var withTrailingBytes = bytes.Concat(new byte[] { 9, 8, 7 }).ToArray();
        GameToolSignature.Verify(withTrailingBytes, source);
        Assert.Equal(GameToolSignature.Digest(bytes, HashAlgorithmName.SHA256),
            GameToolSignature.Digest(withTrailingBytes, HashAlgorithmName.SHA256));
        withTrailingBytes[64] ^= 1;
        Assert.Throws<InvalidDataException>(() => GameToolSignature.Verify(withTrailingBytes, source));
    }

    [Fact]
    public void FailedCachePromotionRestoresThePreviousEntry()
    {
        using var directory = new TemporaryDirectory();
        var cache = Path.Combine(directory.Root, "cache");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "original"), "kept");
        Assert.Throws<DirectoryNotFoundException>(() => GameToolReleases.Promote(
            Path.Combine(directory.Root, "missing-stage"), cache));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(cache, "original")));
    }

    private static (byte[] Bytes, string Thumbprint) SignedSetup()
    {
        var prefix = GameToolLocalTests.Pe(Machine.Amd64);
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var file = zip.CreateEntry("ReShade64.dll").Open();
            file.Write(GameToolLocalTests.Pe(Machine.Amd64));
        }

        var unsigned = prefix.Concat(archive.ToArray()).ToArray();
        using (var reader = new PEReader(new MemoryStream(prefix)))
        {
            var security = reader.PEHeaders.PEHeaderStartOffset + 112 + 32;
            BinaryPrimitives.WriteInt32LittleEndian(unsigned.AsSpan(security), unsigned.Length);
        }

        var content = new AsnWriter(AsnEncodingRules.DER);
        content.PushSequence();
        content.PushSequence();
        content.PopSequence();
        content.PushSequence();
        content.PushSequence();
        content.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1");
        content.WriteNull();
        content.PopSequence();
        content.WriteOctetString(GameToolSignature.Digest(unsigned, HashAlgorithmName.SHA256));
        content.PopSequence();
        content.PopSequence();
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Fixture Publisher", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
        var signed = new SignedCms(new ContentInfo(new Oid("1.3.6.1.4.1.311.2.1.4"), content.Encode()));
        signed.ComputeSignature(new CmsSigner(certificate));
        var cms = signed.Encode();
        var bytes = new byte[unsigned.Length + cms.Length + 8];
        unsigned.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(unsigned.Length), cms.Length + 8);
        cms.CopyTo(bytes, unsigned.Length + 8);
        using (var reader = new PEReader(new MemoryStream(prefix)))
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(reader.PEHeaders.PEHeaderStartOffset + 112 + 36), cms.Length + 8);
        }

        return (bytes, certificate.Thumbprint);
    }
}
