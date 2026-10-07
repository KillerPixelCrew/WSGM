using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace WSGM.Core;

internal sealed class EmulatorPackages(EmulatorNetwork network)
{
    private static readonly Dictionary<string, string[]> SystemAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Nintendo - Nintendo Entertainment System"] = ["nes"], ["nes"] = ["nes"],
        ["Nintendo - Super Nintendo Entertainment System"] = ["snes"], ["super_nes"] = ["snes"], ["snes"] = ["snes"],
        ["Nintendo - Game Boy"] = ["gb"], ["game_boy"] = ["gb"], ["gb"] = ["gb"],
        ["Nintendo - Game Boy Color"] = ["gbc"], ["game_boy_color"] = ["gbc"], ["gbc"] = ["gbc"],
        ["Nintendo - Game Boy Advance"] = ["gba"], ["game_boy_advance"] = ["gba"], ["gba"] = ["gba"],
        ["Nintendo - Nintendo 64"] = ["n64"], ["nintendo_64"] = ["n64"], ["n64"] = ["n64"],
        ["Nintendo - Nintendo DS"] = ["nds"], ["nintendo_ds"] = ["nds"], ["nds"] = ["nds"],
        ["Sony - PlayStation"] = ["psx"], ["playstation"] = ["psx"], ["psx"] = ["psx"],
        ["Sony - PlayStation 2"] = ["ps2"], ["playstation2"] = ["ps2"], ["ps2"] = ["ps2"],
        ["Sony - PlayStation Portable"] = ["psp"], ["psp"] = ["psp"],
        ["Sega - Mega Drive - Genesis"] = ["megadrive"], ["mega_drive"] = ["megadrive"],
        ["genesis"] = ["megadrive"], ["megadrive"] = ["megadrive"],
        ["Sega - Master System - Mark III"] = ["mastersystem"], ["master_system"] = ["mastersystem"],
        ["Sega - Game Gear"] = ["gamegear"], ["game_gear"] = ["gamegear"],
        ["Sega - Sega Saturn"] = ["saturn"], ["saturn"] = ["saturn"],
        ["Sega - Dreamcast"] = ["dreamcast"], ["dreamcast"] = ["dreamcast"],
        ["NEC - PC Engine - TurboGrafx 16"] = ["pcengine"], ["pc_engine"] = ["pcengine"],
        ["arcade"] = ["arcade"], ["MAME"] = ["arcade"]
    };

    public string CoreCatalogueRevision { get; private set; } = "";

    public async Task<string> DownloadAsync(EmulatorAsset asset, string destination,
        CancellationToken cancellationToken)
    {
        if (asset.Name.Length == 0 || Path.GetFileName(asset.Name) != asset.Name
                                   || asset.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException("The emulator asset has an unsafe filename.");
        }

        return await VerifiedDownload.WriteAsync(token => network.OpenAsync(asset.Url, token), destination,
            asset.Sha256, asset.Size, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> RestoreArchiveAsync(EmulatorInstallation installed, string name, string destination,
        string url, string expected, CancellationToken cancellationToken)
    {
        var cache = Path.Combine(installed.PackageCachePath, name);
        if (installed.PackageCachePath.Length > 0 && File.Exists(cache))
        {
            using var input = File.OpenRead(cache);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken)
                .ConfigureAwait(false));
            var recorded = installed.PackageHashes.GetValueOrDefault(name, expected);
            if (recorded.Length == 0 || !hash.Equals(recorded, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The retained repair package failed its recorded checksum: " + name);
            }

            File.Copy(cache, destination);
            return hash;
        }

        return await DownloadAsync(new EmulatorAsset(name, url, expected), destination, cancellationToken)
            .ConfigureAwait(false);
    }

    public static Dictionary<string, string> PreserveArchives(string downloads, string destination)
    {
        Directory.CreateDirectory(destination);
        Dictionary<string, string> hashes = new(StringComparer.Ordinal);
        foreach (var archive in Directory.EnumerateFiles(downloads)
                     .Where(path => Path.GetExtension(path) is ".zip" or ".7z"))
        {
            var name = Path.GetFileName(archive);
            var target = Path.Combine(destination, name);
            File.Move(archive, target);
            using var input = File.OpenRead(target);
            hashes[name] = Convert.ToHexStringLower(SHA256.HashData(input));
        }

        return hashes;
    }

    public async Task<EmulatorCore[]> RepairCoresAsync(EmulatorInstallation installed, string programRoot,
        string downloads, Action<string> progress, CancellationToken cancellationToken)
    {
        foreach (var name in new[] { "info", "assets", "autoconfig", "database-rdb", "database-cursors" })
        {
            var archive = Path.Combine(downloads, name + ".zip");
            // Retained assets are the installed version's assets, not today's moving Buildbot bundle.
            if (installed.PackageCachePath.Length == 0 ||
                !File.Exists(Path.Combine(installed.PackageCachePath, name + ".zip")))
            {
                throw new IOException("This installed version has no retained " + name +
                                      " repair package. Its current program remains intact.");
            }

            await RestoreArchiveAsync(installed, name + ".zip", archive, "", "", cancellationToken)
                .ConfigureAwait(false);
            Extract(archive, Path.Combine(programRoot, name), cancellationToken);
        }

        var cores = new List<EmulatorCore>();
        foreach (var core in installed.Cores)
        {
            var name = Path.GetFileName(core.Path) + ".zip";
            var archive = Path.Combine(downloads, name);
            await RestoreArchiveAsync(installed, name, archive, core.SourceUrl, "", cancellationToken)
                .ConfigureAwait(false);
            var extracted = Path.Combine(downloads, "repair-" + core.Id);
            Extract(archive, extracted, cancellationToken);
            var dll = Directory.EnumerateFiles(extracted, Path.GetFileName(core.Path), SearchOption.AllDirectories)
                .Single();
            var digest = CoreDigests(dll);
            if (!digest.Sha.Equals(core.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The original core version is no longer available for repair: " +
                                               core.Name);
            }

            var target = Path.Combine(programRoot, "cores", Path.GetFileName(core.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(dll, target);
            cores.Add(core with { Path = target });
            progress($"RetroArch: repaired {cores.Count}/{installed.Cores.Length} installed cores");
        }

        return [.. cores];
    }

    public static void Extract(string archivePath, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var buffer = new byte[81920];
        if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                CopyEntry(reader.Entry, reader.OpenEntryStream);
            }
        }
        else
        {
            foreach (var entry in archive.Entries)
            {
                CopyEntry(entry, entry.OpenEntryStream);
            }
        }

        return;

        void CopyEntry(IEntry entry, Func<Stream> open)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.Key?.Replace('/', Path.DirectorySeparatorChar) ?? "";
            var target = Path.GetFullPath(Path.Combine(destination, name));
            if (!StoragePaths.IsUnder(destination, target))
            {
                throw new InvalidDataException("An emulator archive path escapes its private staging directory.");
            }

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(target);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = open();
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
            }
        }
    }

    public static void DeleteOwned(string path, string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!StoragePaths.IsUnder(root, full) || full.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The emulator cleanup target is outside its owned root.");
        }

        if (Directory.Exists(full))
        {
            Directory.Delete(full, true);
        }
    }

    public static string FindExecutable(string root, EmulatorPackageDefinition definition, string architecture)
    {
        var names = definition.ExecutableNames.Where(name => architecture == "arm64"
            ? !name.Contains("x64", StringComparison.OrdinalIgnoreCase)
            : !name.Contains("ARM64", StringComparison.OrdinalIgnoreCase)).ToArray();
        var found = Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories)
            .Where(path => names.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)).ToArray();
        if (found.Length != 1)
        {
            throw new InvalidDataException("The package does not contain one expected " + definition.Name +
                                           " executable.");
        }

        return found[0];
    }

    public async Task<EmulatorCore[]> InstallCoresAsync(string coreRoot, string assetRoot, string programRoot,
        string downloadRoot,
        string dataRoot, Action<string> progress, CancellationToken cancellationToken)
    {
        var plain = await network.TextAsync(coreRoot + ".index", cancellationToken).ConfigureAwait(false);
        var names = plain.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(name => name.Trim()).Where(name => name.EndsWith(".dll.zip", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0)
        {
            throw new InvalidDataException("The complete Libretro core catalogue could not be read.");
        }

        var extended = await network.TextAsync(coreRoot + ".index-extended", cancellationToken).ConfigureAwait(false);
        CoreCatalogueRevision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plain + extended)));
        var crcs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in extended.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length >= 3 && Regex.IsMatch(pieces[1], "^[a-fA-F0-9]{8}$"))
            {
                crcs[pieces[^1]] = pieces[1];
            }
        }

        foreach (var asset in new[] { "info", "assets", "autoconfig", "database-rdb", "database-cursors" })
        {
            progress("RetroArch: installing " + asset);
            var archive = Path.Combine(downloadRoot, asset + ".zip");
            var assetBase = new Uri(assetRoot);
            await DownloadAsync(new EmulatorAsset(asset + ".zip", new Uri(assetBase, asset + ".zip").AbsoluteUri),
                archive, cancellationToken).ConfigureAwait(false);
            Extract(archive, Path.Combine(programRoot, asset), cancellationToken);
        }

        var cores = new EmulatorCore[names.Length];
        var infoFiles = Directory
            .EnumerateFiles(Path.Combine(programRoot, "info"), "*.info", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase);
        using var siblings = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var coreToken = siblings.Token;
        using SemaphoreSlim slots = new(3);
        var done = 0;
        var tasks = names.Select(async (name, index) =>
        {
            await slots.WaitAsync(coreToken).ConfigureAwait(false);
            try
            {
                var archive = Path.Combine(downloadRoot, name);
                await DownloadAsync(new EmulatorAsset(name, coreRoot + Uri.EscapeDataString(name)), archive,
                    coreToken).ConfigureAwait(false);
                var extracted = Path.Combine(downloadRoot, "core-" + index);
                Extract(archive, extracted, coreToken);
                var expectedName = name[..^4];
                var dlls = Directory.EnumerateFiles(extracted, "*.dll", SearchOption.AllDirectories)
                    .Where(path => Path.GetFileName(path).Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (dlls.Length != 1)
                {
                    throw new InvalidDataException("Core package has no unique expected DLL: " + name);
                }

                var digests = CoreDigests(dlls[0]);
                if (crcs.TryGetValue(name, out var expectedCrc) && !digests.Crc.Equals(expectedCrc,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The Libretro core changed or failed its upstream CRC32: " + name);
                }

                var target = Path.Combine(programRoot, "cores", expectedName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(dlls[0], target, true);
                var id = Path.GetFileNameWithoutExtension(expectedName);
                var info = infoFiles.GetValueOrDefault(id);
                var fields = info is null ? new Dictionary<string, string>() : Info(File.ReadAllText(info));
                cores[index] = new EmulatorCore
                {
                    Id = id,
                    Name = fields.GetValueOrDefault("display_name", id),
                    Systems = CoreSystems(id, fields),
                    Extensions = Split(fields.GetValueOrDefault("supported_extensions", "")),
                    Path = target,
                    SourceUrl = coreRoot + name,
                    Sha256 = digests.Sha,
                    MetadataMissing = info is null,
                    RequiredFiles = fields.Where(pair => pair.Key.StartsWith("firmware", StringComparison.Ordinal)
                                                         && pair.Key.EndsWith("_path", StringComparison.Ordinal)
                                                         && fields.GetValueOrDefault(pair.Key[..^5] + "_opt") != "true")
                        .Select(pair => FirmwarePath(dataRoot, pair.Value)).ToArray()
                };
                progress($"RetroArch: installed {Interlocked.Increment(ref done)}/{names.Length} core packages");
            }
            catch
            {
                siblings.Cancel();
                throw;
            }
            finally
            {
                slots.Release();
            }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        var current = await network.TextAsync(coreRoot + ".index", cancellationToken).ConfigureAwait(false);
        if (!plain.Equals(current, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Libretro catalogue changed during installation. Refresh and retry; the previous install is intact.");
        }

        return cores;
    }

    private static Dictionary<string, string> Info(string text)
    {
        return IniFile.ReadTextValues(text)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Trim('"'), StringComparer.Ordinal);
    }

    private static string[] Split(string value)
    {
        return value.Split('|', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string FirmwarePath(string dataRoot, string value)
    {
        return Path.GetFullPath(Path.Combine(dataRoot, "system", value));
    }

    private static string[] CoreSystems(string id, Dictionary<string, string> fields)
    {
        var raw = Split(fields.GetValueOrDefault("systemid", ""));
        var database = Split(fields.GetValueOrDefault("database", ""));
        var systems = raw.Concat(database).SelectMany(value => SystemAliases.GetValueOrDefault(value, [value]))
            .ToList();
        if (id is "mgba_libretro" or "gambatte_libretro" or "sameboy_libretro")
        {
            systems.AddRange(id == "mgba_libretro" ? ["gb", "gbc", "gba"] : ["gb", "gbc"]);
        }

        return systems.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static (string Crc, string Sha) CoreDigests(string path)
    {
        var crc = new Crc32();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var input = File.OpenRead(path);
        var buffer = new byte[81920];
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            crc.Append(buffer.AsSpan(0, count));
            sha.AppendData(buffer.AsSpan(0, count));
        }

        return (crc.GetCurrentHashAsUInt32().ToString("x8", CultureInfo.InvariantCulture),
            Convert.ToHexStringLower(sha.GetHashAndReset()));
    }
}
