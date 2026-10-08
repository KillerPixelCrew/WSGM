using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

internal sealed record EmulatorBiosFile(string Path, string Status, string Md5, bool Optional);

internal sealed record EmulatorBiosSystem(
    string Id,
    string Name,
    string Status,
    string[] Emulators,
    EmulatorBiosFile[] Files,
    string[] Links);

internal sealed record EmulatorBiosState(string Folder, bool Checked, EmulatorBiosSystem[] Systems);

internal sealed record BiosCatalog
{
    public BiosSystemDefinition[] Systems { get; init; } = [];
}

internal sealed record BiosSystemDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Any { get; init; }
    public bool Optional { get; init; }
    public BiosFileDefinition[] Files { get; init; } = [];
}

internal sealed record BiosFileDefinition
{
    public string Path { get; init; } = "";
    public string[] Md5 { get; init; } = [];
    public bool Optional { get; init; }
    public bool Directory { get; init; }
}

/// <summary>The emulator manager's shared local BIOS catalogue, verification and native path configuration.</summary>
public sealed partial class EmulatorManager
{
    private static readonly BiosCatalog BiosDefinitions = LoadBiosCatalog();
    private EmulatorBiosState _bios = new("", false, []);

    private string BiosFolder => GetSnapshot().BiosFolder is { Length: > 0 } path
        ? path
        : Path.Combine(_context.Root, "Emulation", "bios");

    internal EmulatorBiosState ReadBiosState()
    {
        lock (_stateLock)
        {
            return _bios;
        }
    }

    internal Task SetBiosFolderAsync(string path, CancellationToken cancellationToken)
    {
        return OperationAsync(token =>
        {
            path = Path.GetFullPath(path);
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException("Choose an existing BIOS folder.");
            }

            Mutate(store => store with { BiosFolder = path }, false);
            VerifyBios(token);
            SetStatus("BIOS folder changed. Use Relink emulators to apply it to installed emulators.");
            return Task.CompletedTask;
        }, cancellationToken);
    }

    internal Task VerifyBiosAsync(CancellationToken cancellationToken)
    {
        return OperationAsync(token =>
        {
            VerifyBios(token);
            Mutate(store => store with
            {
                Installations = store.Installations.Select(EmulatorPrerequisites.RefreshState).ToArray()
            });
            SetStatus("BIOS files checked against the bundled retrobios EmuDeck metadata.");
            return Task.CompletedTask;
        }, cancellationToken);
    }

    internal Task AddBiosFilesAsync(string path, string systemId, CancellationToken cancellationToken)
    {
        return OperationAsync(token =>
        {
            path = Path.GetFullPath(path);
            var folder = BiosFolder;
            if (systemId.Length > 0 && !BiosDefinitions.Systems.Any(system => system.Id == systemId))
            {
                throw new InvalidOperationException("Choose a supported BIOS system.");
            }

            if (Directory.Exists(path) && StoragePaths.IsUnder(path, folder)
                                       && !Path.GetFullPath(path).Equals(Path.GetFullPath(folder),
                                           StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Choose the source BIOS folder, not a parent containing the destination.");
            }

            Directory.CreateDirectory(folder);
            var definitions = BiosDefinitions.Systems.Where(system => systemId.Length == 0 || system.Id == systemId)
                .SelectMany(system => system.Files).Where(file => !file.Directory).ToArray();
            var files = Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint,
                    IgnoreInaccessible = false
                })
                : File.Exists(path)
                    ? [path]
                    : throw new FileNotFoundException("Choose local BIOS files or a folder.");
            var copied = 0;
            var firmwareAdded = new HashSet<string>();
            var visited = 0;
            foreach (var source in files)
            {
                token.ThrowIfCancellationRequested();
                if (++visited > 4096)
                {
                    throw new InvalidDataException("Choose a BIOS folder containing at most 4096 files.");
                }

                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Choose regular BIOS files rather than file links.");
                }

                var name = Path.GetFileName(source);
                var candidates = definitions.Where(file => Path.GetFileName(file.Path)
                    .Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                var relative = Directory.Exists(path) ? Path.GetRelativePath(path, source).Replace('\\', '/') : name;
                var match = candidates.FirstOrDefault(file =>
                    file.Path.Equals(relative, StringComparison.OrdinalIgnoreCase));
                match ??= candidates.Length == 1 ? candidates[0] : null;
                if (match is null && candidates.Length > 1)
                {
                    throw new InvalidDataException("Preserve the EmuDeck subfolder for " + name + " when adding it.");
                }

                var switchFirmware = systemId == "switch" || systemId.Length == 0;
                var isFirmware = switchFirmware &&
                                 Path.GetExtension(source).Equals(".nca", StringComparison.OrdinalIgnoreCase);
                var isFirmwareZip = match is null && switchFirmware &&
                                    Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase);
                if (isFirmwareZip && match is null && systemId.Length == 0)
                {
                    using var archive = ZipFile.OpenRead(source);
                    isFirmwareZip = archive.Entries.Any(entry =>
                        entry.Name.EndsWith(".nca", StringComparison.OrdinalIgnoreCase));
                }

                if (match is null && !isFirmware && !isFirmwareZip && new FileInfo(source).Length <= 64 * 1024 * 1024)
                {
                    // Recognize renamed console dumps by the actual MD5, not their chosen filename.
                    var digest = BiosMd5(source, token);
                    match = definitions.FirstOrDefault(file =>
                        file.Md5.Contains(digest, StringComparer.OrdinalIgnoreCase));
                }

                var destination = match is not null ? Path.Combine(folder, match.Path)
                    : isFirmwareZip ? Path.Combine(folder, "switch", "firmware.zip")
                    : isFirmware ? Path.Combine(folder, "switch", "firmware", name) : null;
                if (destination is null)
                {
                    continue;
                }

                if (!Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var input = File.OpenRead(source);
                    AtomicFile.Write(destination, output =>
                    {
                        input.CopyTo(output);
                        return true;
                    }, true);
                }

                copied++;
                if (isFirmware || isFirmwareZip)
                {
                    firmwareAdded.Add("switch");
                }

                if (name.Equals("PS3UPDAT.PUP", StringComparison.OrdinalIgnoreCase))
                {
                    firmwareAdded.Add("ps3");
                }
            }

            VerifyBios(token);
            if (copied == 0)
            {
                throw new InvalidDataException(
                    "No recognized BIOS files were found. Preserve the EmuDeck folder layout.");
            }

            RelinkBios(token, systemId, installFirmware: firmwareAdded);
            Mutate(store => store with { BiosFolder = folder }, false);
            VerifyBios(token);
            SetStatus(
                $"Added {copied} BIOS files and linked installed emulators. Firmware packages use Install firmware.");
            return Task.CompletedTask;
        }, cancellationToken);
    }

    internal Task RelinkBiosAsync(string systemId, CancellationToken cancellationToken)
    {
        return OperationAsync(token =>
        {
            RelinkBios(token, systemId);
            VerifyBios(token);
            SetStatus("Installed emulators linked to the shared BIOS folder. Existing files were preserved.");
            return Task.CompletedTask;
        }, cancellationToken);
    }

    private void VerifyBios(CancellationToken token)
    {
        var folder = BiosFolder;
        var snapshot = GetSnapshot();
        var systems = BiosDefinitions.Systems.Where(system => snapshot.Definitions.Any(definition =>
            definition.Systems.Contains(system.Id))).Select(system =>
        {
            var files = system.Files.Select(file =>
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(folder, file.Path);
                var present = file.Directory
                    ? Directory.Exists(path)
                      && Directory.EnumerateFiles(path, "*.nca").Any()
                    : File.Exists(path);
                if (!present && system.Id == "switch" && file.Directory)
                {
                    present = File.Exists(Path.Combine(folder, "switch", "firmware.zip"));
                }

                var md5 = present && !file.Directory && file.Md5.Length > 0 ? BiosMd5(path, token) : "";
                var status = !present ? "Missing"
                    : file.Md5.Length == 0 ? "Present"
                    : file.Md5.Contains(md5, StringComparer.OrdinalIgnoreCase) ? "Verified" : "Wrong file";
                return new EmulatorBiosFile(file.Path, status, md5, file.Optional || system.Optional);
            }).ToArray();
            var required = files.Where(file => !file.Optional).ToArray();
            var ready = system.Any
                ? files.Any(file => file.Status is "Verified" or "Present")
                : required.All(file => file.Status is "Verified" or "Present");
            var status = files.Any(file => file.Status == "Wrong file")
                ? "Wrong file"
                : system.Optional
                    ? "Optional"
                    : ready
                        ? files.Any(file => file.Status == "Present") ? "Present" : "Verified"
                        : files.Any(file => file.Status is "Present" or "Verified")
                            ? "Partial"
                            : "Missing";
            var installed = snapshot.Installations.Where(item => item.Systems.Contains(system.Id)).ToArray();
            return new EmulatorBiosSystem(system.Id, system.Name, status,
                installed.Select(item => item.Name).Distinct().ToArray(), files,
                installed.SelectMany(item => item.ConfiguredPrerequisites.Select(pair => item.Name + " · "
                    + pair.Key + " → " + pair.Value)).ToArray());
        }).ToArray();
        lock (_stateLock)
        {
            _bios = new EmulatorBiosState(folder, true, systems);
        }

        NotifyChanged();
    }

    private void RelinkBios(CancellationToken token, string systemId = "", string installationId = "",
        HashSet<string>? installFirmware = null)
    {
        if (systemId.Length > 0 && !BiosDefinitions.Systems.Any(system => system.Id == systemId))
        {
            throw new InvalidOperationException("Choose a supported BIOS system.");
        }

        var folder = BiosFolder;
        Directory.CreateDirectory(folder);
        Admission(token, () =>
        {
            var installations = EmulatorStorage.ReadInstallations(_context.Root).ToArray();
            foreach (var installed in installations.Where(item =>
                         (systemId.Length == 0 || item.Systems.Contains(systemId))
                         && (installationId.Length == 0 || item.Id == installationId)))
            {
                EmulatorPrerequisites.EnsureStopped(installed);
            }

            for (var index = 0; index < installations.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var installed = installations[index];
                if ((systemId.Length > 0 && !installed.Systems.Contains(systemId))
                    || (installationId.Length > 0 && installed.Id != installationId))
                {
                    continue;
                }

                var configured = new Dictionary<string, string>(installed.ConfiguredPrerequisites);
                foreach (var rule in installed.DataPolicy.Prerequisites.Where(rule => !rule.NativeInstaller))
                {
                    var target = installed.DefinitionId == "dolphin" ? Path.Combine(folder, "GC") : folder;
                    if (installed.DefinitionId == "dolphin")
                    {
                        LinkBiosDirectory(Path.Combine(installed.DataPath, "GC"), target);
                    }
                    else if (installed.DefinitionId == "eden")
                    {
                        LinkBiosDirectory(Path.Combine(installed.DataPath, "keys"), target);
                    }
                    else if (installed.DataPolicy.HasCores)
                    {
                        IniFile.SetValue(Path.Combine(installed.DataPath, installed.DataPolicy.ConfigFile),
                            "system_directory", '"' + target.Replace('\\', '/') + '"');
                        LinkBiosDirectory(Path.Combine(installed.DataPath, "system"), target);
                    }
                    else if (rule.IniFile.Length > 0)
                    {
                        IniFile.SetValue(Path.Combine(installed.DataPath, rule.IniFile), rule.IniKey, target,
                            rule.IniSection);
                    }

                    configured[rule.Kind] = target;
                }

                installed = installed with { ConfiguredPrerequisites = configured };
                foreach (var rule in installed.DataPolicy.Prerequisites.Where(rule => rule.NativeInstaller))
                {
                    var source = installed.DefinitionId == "rpcs3"
                        ? Path.Combine(folder, "PS3UPDAT.PUP")
                        : File.Exists(Path.Combine(folder, "switch", "firmware.zip"))
                            ? Path.Combine(folder, "switch", "firmware.zip")
                            : Path.Combine(folder, "switch", "firmware");
                    if ((File.Exists(source) || Directory.Exists(source))
                        && (installed.Systems.Any(system => installFirmware?.Contains(system) == true)
                            || !EmulatorStorage.PrerequisitePresent(installed, rule))
                        && (installed.DefinitionId != "eden" || File.Exists(Path.Combine(folder, "prod.keys"))))
                    {
                        installed = EmulatorPrerequisites.Configure(installed, source, rule.Kind, token);
                    }
                }

                installations[index] = EmulatorPrerequisites.RefreshState(installed);
            }

            Mutate(store => store with { Installations = installations });
        });
    }

    private static void LinkBiosDirectory(string link, string target)
    {
        if (Path.GetFullPath(link).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (StoragePaths.IsUnder(link, target))
        {
            throw new InvalidOperationException(
                "Choose a shared BIOS folder outside the emulator's existing system folder.");
        }

        Directory.CreateDirectory(target);
        var directory = new DirectoryInfo(link);
        string? backup = null;
        if (directory.Exists || directory.LinkTarget is not null)
        {
            if (directory.LinkTarget is not null)
            {
                if (directory.ResolveLinkTarget(true)?.FullName.Equals(Path.GetFullPath(target),
                        StringComparison.OrdinalIgnoreCase) == true)
                {
                    return;
                }
            }
            else
            {
                foreach (var file in Directory.EnumerateFiles(link, "*", new EnumerationOptions
                             { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    var destination = Path.Combine(target, Path.GetRelativePath(link, file));
                    if (!File.Exists(destination))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(file, destination);
                    }
                }

                // Retain the original directory beside the link; never erase an emulator's data.
            }

            backup = link + ".before-wsgm-bios-" + Guid.NewGuid().ToString("N");
            Directory.Move(link, backup);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (link.Contains('"') || target.Contains('"') || link.Contains('%') || target.Contains('%'))
                {
                    throw new IOException("The BIOS path cannot be used for a directory junction.", ex);
                }

                var start = new ProcessStartInfo("cmd.exe")
                {
                    Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"", CreateNoWindow = true,
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
                };
                if (!ThemePaths.RunJunctionCommand(start, TimeSpan.FromSeconds(10)))
                {
                    throw new IOException("The emulator BIOS folder could not be linked.");
                }
            }
        }
        catch
        {
            if (backup is not null && Directory.Exists(backup))
            {
                if (new DirectoryInfo(link).LinkTarget is not null)
                {
                    Directory.Delete(link);
                }

                Directory.Move(backup, link);
            }

            throw;
        }
    }

    private static string BiosMd5(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        if (stream.Length > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("A BIOS file exceeds the 64 MiB verification limit: " +
                                           Path.GetFileName(path));
        }

        return Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
    }

    private static BiosCatalog LoadBiosCatalog()
    {
        using var stream =
            typeof(EmulatorManager).Assembly.GetManifestResourceStream("WSGM.Core.Emulators.bios-catalog.json")!;
        return JsonSerializer.Deserialize<BiosCatalog>(stream, EmulatorStorage.JsonOptions)!;
    }
}
