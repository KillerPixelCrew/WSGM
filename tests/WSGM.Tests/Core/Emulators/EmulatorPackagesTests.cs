using System.IO.Compression;
using System.Security.Cryptography;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core;

public sealed class EmulatorPackagesTests
{
    [Fact]
    public void Extract_SevenZipCopiesEverySequentialEntry()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = temporary.GetPath("package.7z");
        using (var output = File.Create(archivePath))
        using (var writer = WriterFactory.OpenWriter(output, ArchiveType.SevenZip,
                   new SevenZipWriterOptions(CompressionType.LZMA2)))
        {
            using var first = new MemoryStream("first entry"u8.ToArray());
            using var second = new MemoryStream("second entry"u8.ToArray());
            writer.Write("nested/first.txt", first, DateTime.UtcNow);
            writer.Write("nested/second.txt", second, DateTime.UtcNow);
        }

        var destination = temporary.GetPath("program");
        EmulatorPackages.Extract(archivePath, destination, CancellationToken.None);

        Assert.Equal("first entry", File.ReadAllText(Path.Combine(destination, "nested", "first.txt")));
        Assert.Equal("second entry", File.ReadAllText(Path.Combine(destination, "nested", "second.txt")));
    }

    [Fact]
    public void Extract_AllowsNormalizedPathsAndDuplicateEntriesButRejectsEscape()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = temporary.GetPath("package.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using (var first = new StreamWriter(archive.CreateEntry("./emulator.txt").Open()))
            {
                first.Write("first");
            }

            using (var second = new StreamWriter(archive.CreateEntry("emulator.txt").Open()))
            {
                second.Write("second");
            }
        }

        var output = temporary.GetPath("program");
        EmulatorPackages.Extract(archivePath, output, CancellationToken.None);
        Assert.Equal("second", File.ReadAllText(Path.Combine(output, "emulator.txt")));

        var escaping = temporary.GetPath("escaping.zip");
        using (var archive = ZipFile.Open(escaping, ZipArchiveMode.Create))
        {
            using var entry = new StreamWriter(archive.CreateEntry("../outside.txt").Open());
            entry.Write("outside");
        }

        Assert.Throws<InvalidDataException>(() => EmulatorPackages.Extract(escaping, output, CancellationToken.None));
        Assert.False(File.Exists(temporary.GetPath("outside.txt")));
    }

    [Fact]
    public async Task RestoreArchive_UsesRetainedAssetNameWithoutFetchingMovingNightlyUrl()
    {
        using var temporary = new TemporaryDirectory();
        var cache = temporary.GetPath("packages");
        Directory.CreateDirectory(cache);
        var name = "RetroArch.7z";
        var bytes = "original installed package"u8.ToArray();
        File.WriteAllBytes(Path.Combine(cache, name), bytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var installation = new EmulatorInstallation
        {
            PackageName = name, PackageCachePath = cache,
            PackageHashes = new Dictionary<string, string> { [name] = hash }
        };
        using var network = new EmulatorNetwork(null);
        var packages = new EmulatorPackages(network);
        var destination = temporary.GetPath("repaired.7z");

        Assert.Equal(hash, await packages.RestoreArchiveAsync(installation, installation.PackageName, destination,
            "https://invalid.example/pruned-nightly-RetroArch.7z", "", CancellationToken.None));
        Assert.Equal(bytes, File.ReadAllBytes(destination));
    }
}
