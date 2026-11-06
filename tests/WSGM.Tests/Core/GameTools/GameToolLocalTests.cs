using System.IO.Compression;
using System.Net;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using WSGM.Core;
using WSGM.Testing;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Core;

public sealed class GameToolLocalTests
{
    [Fact]
    public void SetupZipOffsetsRemainRelativeAfterRemovingThePePrefixAndTrailingCertificate()
    {
        using var directory = new TemporaryDirectory();
        var prefix = Pe(Machine.Amd64);
        byte[] zipBytes;
        using (var memory = new MemoryStream())
        {
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(zip.CreateEntry("ReShade64.dll").Open());
                writer.Write("fixture library");
            }

            zipBytes = memory.ToArray();
        }

        using (var pe = new PEReader(new MemoryStream(prefix)))
        {
            var security = pe.PEHeaders.PEHeaderStartOffset + 112 + 32;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(security), prefix.Length + zipBytes.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(security + 4), 128);
        }

        var file = Path.Combine(directory.Root, "setup.exe");
        File.WriteAllBytes(file, [.. prefix, .. zipBytes, .. new byte[128]]);
        using var embedded = GameToolReleases.ReShadeZip(file);
        using var archive = new ZipArchive(embedded, ZipArchiveMode.Read);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("ReShade64.dll", entry.FullName);
        using var reader = new StreamReader(entry.Open());
        Assert.Equal("fixture library", reader.ReadToEnd());
    }

    [Fact]
    public async Task OnlyTheSelectedArchitectureAndApiReachTheGameAndTheCacheIsVerified()
    {
        using var directory = new TemporaryDirectory();
        var tool = GameToolCatalog.Current.Find("dgvoodoo2")!;
        byte[] bytes;
        using (var memory = new MemoryStream())
        {
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var entry = zip.CreateEntry("MS/x64/D3D9.dll").Open())
                {
                    entry.Write(Pe(Machine.Amd64));
                }

                using (var entry = zip.CreateEntry("MS/x86/D3D8.dll").Open())
                {
                    entry.Write(Pe(Machine.I386));
                }

                using var config = new StreamWriter(zip.CreateEntry("dgVoodoo.conf").Open());
                config.Write("[General]\nOutputAPI=d3d11\n");
            }

            bytes = memory.ToArray();
        }

        var calls = 0;
        using var handler = new StubHttpMessageHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        using var releases = new GameToolReleases(directory.Root, handler);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var release = new HostedRelease("2.87.6", "test", "", "GitHub",
            new ReleaseAsset("dgVoodoo2_87_6.zip", "https://example.test/tool.zip", sha, bytes.Length));
        var package = await releases.PrepareAsync(tool, tool.DefaultChannel, release, (_, _, _) => { },
            CancellationToken.None, "x64", "D3D9.dll");
        Assert.Equal(["WSGM-tool.dll", "dgVoodoo.conf"],
            Directory.GetFiles(package.Root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var cached = await releases.PrepareAsync(tool, tool.DefaultChannel, release, (_, _, _) => { },
            CancellationToken.None, "x64", "D3D9.dll");
        Assert.Equal(package.Root, cached.Root);
        Assert.Equal(1, calls);
        File.WriteAllText(Path.Combine(package.Root, tool.PrimaryFile), "corrupt");
        await releases.PrepareAsync(tool, tool.DefaultChannel, release, (_, _, _) => { },
            CancellationToken.None, "x64", "D3D9.dll");
        Assert.Equal(2, calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => releases.PrepareAsync(tool, tool.DefaultChannel,
            release, (_, _, _) => { }, CancellationToken.None, "x64", "D3D8.dll"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DisableRestoresOriginalsEnableRestoresAllBinariesAndRemovalKeepsUserFiles()
    {
        using var directory = new TemporaryDirectory();
        var tool = GameToolCatalog.Current.Find("dxvk")! with { Replaceable = ["d3d11.dll"] };
        var game = Path.Combine(directory.Root, "game");
        var payload = Path.Combine(directory.Root, "payload");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(game, "game.exe"), "program");
        File.WriteAllText(Path.Combine(game, "d3d11.dll"), "original game file");
        File.WriteAllText(Path.Combine(game, "notes.txt"), "user file");
        File.WriteAllText(Path.Combine(payload, tool.PrimaryFile), "injector");
        File.WriteAllText(Path.Combine(payload, "dxgi.dll"), "support");
        var release = new HostedRelease("3.1", "test", "", "GitHub",
            new ReleaseAsset("test.tar.gz", "https://example.test/test.tar.gz", new string('a', 64)));
        var package = new GameToolPackage(release, payload, "3.1", "", "x64");
        var target = new GameToolTarget(218, game, Path.Combine(game, "game.exe"), "x64", "unknown");
        var data = Path.Combine(directory.Root, "data");
        var installer = new GameToolInstaller(data);
        var outcome = await installer.InstallAsync(tool, target, package,
            new GameToolRequest("install", "d3d11.dll", new Dictionary<string, string>()), null,
            () => null, _ => { }, CancellationToken.None);
        installer.SetEnabled(tool, target, outcome.Manifest!, enabled: false, () => null);
        var disabled = GameToolStorage.Read(data).Find(218)!.Manifest("dxvk")!;
        Assert.False(disabled.Enabled);
        Assert.Equal("original game file", File.ReadAllText(Path.Combine(game, "d3d11.dll")));
        Assert.False(File.Exists(Path.Combine(game, "dxgi.dll")));
        Assert.Empty(GameToolInstaller.Inspect(tool, game, disabled).Missing);
        Assert.Throws<GameToolException>(() => installer.SetEnabled(tool, target, disabled, enabled: true,
            () => "The game is running."));
        Assert.False(GameToolStorage.Read(data).Find(218)!.Manifest("dxvk")!.Enabled);
        installer.SetEnabled(tool, target, disabled, enabled: true, () => null);
        var enabled = GameToolStorage.Read(data).Find(218)!.Manifest("dxvk")!;
        Assert.True(enabled.Enabled);
        Assert.Equal("injector", File.ReadAllText(Path.Combine(game, "d3d11.dll")));
        Assert.Equal("support", File.ReadAllText(Path.Combine(game, "dxgi.dll")));
        installer.SetEnabled(tool, target, enabled, enabled: false, () => null);
        disabled = GameToolStorage.Read(data).Find(218)!.Manifest("dxvk")!;
        installer.Remove(tool, target, disabled);
        Assert.Equal("original game file", File.ReadAllText(Path.Combine(game, "d3d11.dll")));
        Assert.Equal("user file", File.ReadAllText(Path.Combine(game, "notes.txt")));
        Assert.DoesNotContain(Directory.GetFiles(game), path => path.Contains("wsgm-disabled", StringComparison.Ordinal));
        Assert.Null(GameToolStorage.Read(data).Find(218)?.Manifest("dxvk"));
    }

    [Theory]
    [InlineData("dxvk", "DXVK")]
    [InlineData("reshade", "ReShade")]
    [InlineData("special-k", "Special K")]
    [InlineData("dgvoodoo2", "dgVoodoo")]
    public void PublisherMetadataIdentifiesToolsWithoutMistakingMicrosoftDlls(string id, string publisher)
    {
        var tool = GameToolCatalog.Current.Find(id)!;
        Assert.True(GameToolIdentityCheck.IsTool(tool,
            new PeVersionInfo("d3d11.dll", "", "", "", publisher, "", "")));
        Assert.False(GameToolIdentityCheck.IsTool(tool,
            new PeVersionInfo("d3d11.dll", "", "", "Direct3D 11 Runtime", "Microsoft Windows", "", "Microsoft")));
    }

    [Theory]
    [InlineData("dxvk")]
    [InlineData("reshade")]
    [InlineData("special-k")]
    [InlineData("dgvoodoo2")]
    public void LocalToolsHaveExplicitPayloadsAndDoNotPermitUnknownDllReplacement(string id)
    {
        var tool = GameToolCatalog.Current.Find(id)!;
        Assert.NotEmpty(tool.Modes);
        Assert.False(tool.IsReplaceable("dxgi.dll"));
        Assert.All(tool.Modes, mode => Assert.Contains(tool.PrimaryFile, mode.Files.Values));
    }

    private static byte[] Pe(Machine machine)
    {
        var image = new BlobBuilder();
        var pe = new ManagedPEBuilder(new PEHeaderBuilder(machine: machine,
                imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(new MetadataBuilder()), new BlobBuilder());
        pe.Serialize(image);
        return image.ToArray();
    }
}
