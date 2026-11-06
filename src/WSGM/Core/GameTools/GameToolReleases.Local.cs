using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

internal sealed partial class GameToolReleases
{
    // The website adapter is deliberately limited to the official standard ReShade distribution.
    private async Task<HostedRelease> WebsiteReleaseAsync(GameToolSource source, CancellationToken token)
    {
        if (source.DownloadPage != "https://reshade.me/")
        {
            throw new InvalidDataException("Unsupported official download page.");
        }

        var page = await _client.TextAsync(source.DownloadPage, token).ConfigureAwait(false);
        var names = Regex.Matches(page, @"ReShade_Setup_(?<version>[0-9]+(?:\.[0-9]+)+)\.exe",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Select(match => match.Value).Distinct().ToArray();
        if (names.Length != 1)
        {
            throw new InvalidDataException("The official ReShade download could not be uniquely identified.");
        }

        var name = names[0];
        var version = name["ReShade_Setup_".Length..^4];
        return new HostedRelease(version, version, source.DownloadPage, "Website",
            new ReleaseAsset(name, "https://reshade.me/downloads/" + name, ""));
    }

    private async Task<GameToolPackage> PrepareLocalAsync(GameToolDefinition tool, GameToolChannel channel,
        HostedRelease release, string architecture, string proxy, Action<string, long, long> progress,
        CancellationToken token)
    {
        var mode = tool.Modes.SingleOrDefault(item => item.Proxy.Equals(proxy, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidDataException("Choose a graphics API supported by " + tool.Name + ".");
        if (!mode.Architectures.Contains(architecture, StringComparer.Ordinal))
        {
            throw new InvalidDataException(mode.Api + " is not supported for " + PeImage.Label(architecture) + " games.");
        }

        var isReShade = tool.Id == "reshade" && channel.Source.Provider == "Website";
        if (!isReShade && UpdateChecker.ParseHash(release.Asset.Sha256) is null)
        {
            throw new InvalidDataException("The publisher did not provide a checksum for this download.");
        }

        var cache = Path.Combine(GameToolStorage.DataRoot(_userRoot), "packages", tool.Id,
            release.Asset.Sha256 + "-" + architecture + "-" + proxy);
        if (UpdateChecker.ParseHash(release.Asset.Sha256) is not null && Directory.Exists(cache))
        {
            var hashes = ReadHashes(cache);
            if (hashes.Count > 0 && mode.Files.Values.All(hashes.ContainsKey)
                && hashes.All(file => File.Exists(Path.Combine(cache, "payload", file.Key))
                                      && GameToolInstaller.Sha256(Path.Combine(cache, "payload", file.Key)) == file.Value.Sha256))
            {
                progress(GameToolPhase.Verify, 0, 0);
                return new GameToolPackage(release, Path.Combine(cache, "payload"), release.Version, "", architecture) { Hashes = hashes };
            }
        }

        var stage = Path.Combine(GameToolStorage.DataRoot(_userRoot), "packages", tool.Id,
            ".stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var archive = Path.Combine(stage, release.Asset.Name);
            await DownloadAsync(release.Asset, archive,
                bytes => progress(GameToolPhase.Download, bytes, release.Asset.Size), token).ConfigureAwait(false);
            progress(GameToolPhase.Verify, 0, 0);
            if (isReShade)
            {
                GameToolSignature.VerifyReShade(archive);
            }

            release = release with { Asset = release.Asset with { Sha256 = GameToolInstaller.Sha256(archive) } };
            var extracted = Path.Combine(stage, "extracted");
            if (channel.Source.PackageType == "exe-zip")
            {
                // ReShade appends a ZIP to its setup PE. It is data, never an invoked installer or script.
                using var zipBytes = ReShadeZip(archive);
                using var zip = new ZipArchive(zipBytes, ZipArchiveMode.Read);
                foreach (var entry in zip.Entries)
                {
                    var path = Path.GetFullPath(Path.Combine(extracted, entry.FullName.Replace('/', '\\')));
                    if (!StoragePaths.IsUnder(extracted, path))
                    {
                        throw new InvalidDataException("A setup archive entry escapes its staging directory.");
                    }

                    if (entry.Name.Length > 0)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        entry.ExtractToFile(path);
                    }
                }
            }
            else if (channel.Source.PackageType == "tar.gz")
            {
                Directory.CreateDirectory(extracted);
                using var input = File.OpenRead(archive);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var tar = new TarReader(gzip);
                while (tar.GetNextEntry() is { } entry)
                {
                    token.ThrowIfCancellationRequested();
                    var path = Path.GetFullPath(Path.Combine(extracted, entry.Name.Replace('/', '\\')));
                    if (entry.EntryType == TarEntryType.Directory)
                    {
                        if (StoragePaths.Same(extracted, path) || StoragePaths.IsUnder(extracted, path))
                        {
                            Directory.CreateDirectory(path);
                            continue;
                        }
                    }

                    if (!StoragePaths.IsUnder(extracted, path)
                        || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    {
                        throw new InvalidDataException("An archive entry is a link or escapes its staging directory.");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    entry.ExtractToFile(path, overwrite: false);
                }
            }
            else
            {
                await Task.Run(() => PackageArchive.Extract(archive, extracted, token), token).ConfigureAwait(false);
            }

            var root = Unwrap(extracted);
            var payload = Path.Combine(stage, "payload");
            Directory.CreateDirectory(payload);
            foreach (var (pattern, destination) in mode.Files)
            {
                token.ThrowIfCancellationRequested();
                var relative = pattern.Replace("{arch}", architecture == "x86" && tool.Id == "dxvk" ? "x32" : architecture,
                    StringComparison.Ordinal).Replace("{bits}", architecture == "x64" ? "64" : "32", StringComparison.Ordinal);
                var source = Path.GetFullPath(Path.Combine(root, relative));
                var target = Path.GetFullPath(Path.Combine(payload, destination));
                if (!StoragePaths.IsUnder(root, source) || !StoragePaths.IsUnder(payload, target) || !File.Exists(source))
                {
                    throw new InvalidDataException("The downloaded package is missing " + relative + ".");
                }

                if (destination.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    && PeImage.TryArchitecture(source) != architecture)
                {
                    throw new InvalidDataException("The downloaded " + relative + " has the wrong architecture.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }

            // A normalized package contains exactly the selected API's files; the transaction still owns every copy.
            var hashes = HashPayload(payload);
            WriteHashes(stage, hashes);
            var final = Path.Combine(GameToolStorage.DataRoot(_userRoot), "packages", tool.Id,
                release.Asset.Sha256 + "-" + architecture + "-" + proxy);
            if (Directory.Exists(final))
            {
                GameToolStorage.DeleteTree(final);
            }

            File.Delete(archive);
            GameToolStorage.DeleteTree(extracted);
            Directory.Move(stage, final);
            return new GameToolPackage(release, Path.Combine(final, "payload"), release.Version, "", architecture) { Hashes = hashes };
        }
        finally
        {
            GameToolStorage.DeleteTree(stage);
        }
    }

    // ReShade's setup concatenates a ZIP and its signing certificate after the PE. ZIP offsets are relative
    // to the ZIP start, which Python tolerates but .NET's ZipArchive does not. Remove only the verified prefix
    // and certificate, preserving every archive-relative offset and the untrusted entry validation above.
    /// <summary>Isolates the setup's archive from its PE prefix and trailing certificate.</summary>
    /// <param name="path">The setup whose Authenticode signature has already been checked.</param>
    /// <returns>The ZIP bytes with archive-relative offsets intact.</returns>
    internal static MemoryStream ReShadeZip(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var pe = new PEReader(new MemoryStream(bytes));
        var certificate = pe.PEHeaders.PEHeader?.CertificateTableDirectory.RelativeVirtualAddress ?? 0;
        var end = certificate > 0 ? certificate : bytes.Length;
        for (var index = end - 22; index >= Math.Max(0, end - 65557); index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4)) != 0x06054b50)
            {
                continue;
            }

            var comment = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(index + 20, 2));
            var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index + 12, 4));
            var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index + 16, 4));
            var start = (long)index - directorySize - directoryOffset;
            if (index + 22 + comment > end || start < 0 || start > index - 4
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)start, 4)) != 0x04034b50)
            {
                continue;
            }

            return new MemoryStream(bytes, (int)start, index + 22 + comment - (int)start, writable: false);
        }

        throw new InvalidDataException("The signed ReShade setup has no valid embedded ZIP.");
    }
}
