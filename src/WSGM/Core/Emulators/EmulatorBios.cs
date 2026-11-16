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
            CheckAndLinkBios(token);
            SetBiosCheckStatus();
            return Task.CompletedTask;
        }, cancellationToken);
    }

    internal Task VerifyBiosAsync(CancellationToken cancellationToken)
    {
        return OperationAsync(token =>
        {
            CheckAndLinkBios(token);
            Mutate(store => store with
            {
                Installations = store.Installations.Select(EmulatorPrerequisites.RefreshState).ToArray()
            });
            SetBiosCheckStatus();
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
                    .Equals(name, StringComparison.OrdinalIgnoreCase)
                    || file.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();
                var relative = Directory.Exists(path) ? Path.GetRelativePath(path, source).Replace('\\', '/') : name;
                var match = candidates.FirstOrDefault(file =>
                    file.Path.Equals(relative, StringComparison.OrdinalIgnoreCase));
                match ??= candidates.Length == 1 ? candidates[0] : null;
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

                if (!isFirmware && !isFirmwareZip && new FileInfo(source).Length <= 64 * 1024 * 1024)
                {
                    // Recognize renamed console dumps by the actual MD5, not their chosen filename.
                    var digest = BiosMd5(source, token);
                    if (match is null || match.Md5.Length > 0
                        && !match.Md5.Contains(digest, StringComparer.OrdinalIgnoreCase))
                    {
                        match = definitions.FirstOrDefault(file =>
                            file.Md5.Contains(digest, StringComparer.OrdinalIgnoreCase)) ?? match;
                    }
                }

                if (match is null && candidates.Length > 1)
                {
                    throw new InvalidDataException("Preserve the EmuDeck subfolder for " + name + " when adding it.");
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

            if (copied == 0)
            {
                throw new InvalidDataException(
                    "No recognized BIOS files were found. Preserve the EmuDeck folder layout.");
            }

            // The copied files are shown verified even when linking them to an emulator fails.
            try
            {
                RelinkBios(token, systemId, installFirmware: firmwareAdded);
                Mutate(store => store with { BiosFolder = folder }, false);
            }
            finally
            {
                VerifyBios(token);
            }

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

    private void SetBiosCheckStatus()
    {
        var missing = GetSnapshot().Installations.Where(item => item.DataPolicy.HasCores)
            .SelectMany(item => item.Cores.SelectMany(core => core.RequiredFiles)
                .Where(path => !File.Exists(path) && !Directory.Exists(path))
                .Select(path => Path.GetRelativePath(Path.Combine(item.DataPath, "system"), path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        SetStatus("BIOS files checked and linked to installed emulators. "
                  + (missing.Length == 0 ? "" : missing.Length + " core files still missing: "
                      + string.Join(", ", missing.Take(8)) + ". ")
                  + "Native firmware uses Install firmware.");
    }

    private static void ArrangeCoreBios(EmulatorInstallation installed, string folder, CancellationToken token)
    {
        var system = Path.Combine(installed.DataPath, "system");
        foreach (var required in installed.Cores.SelectMany(core => core.RequiredFiles)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (!StoragePaths.IsUnder(system, required))
            {
                continue;
            }

            var destination = Path.Combine(folder, Path.GetRelativePath(system, required));
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                continue;
            }

            // Core metadata owns the required filename and subfolder. Reuse a supplied root
            // file without guessing between unrelated same-named dumps in subfolders.
            var source = Path.Combine(folder, Path.GetFileName(required));
            if (!File.Exists(source))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
        }
    }

    private void CheckAndLinkBios(CancellationToken token)
    {
        VerifyBios(token);
        try
        {
            RelinkBios(token, installNativeFirmware: false);
        }
        finally
        {
            VerifyBios(token);
        }
    }

    private void VerifyBios(CancellationToken token)
    {
        var folder = BiosFolder;
        var snapshot = GetSnapshot();
        // Index once: BIOS collections commonly keep console subfolders or renamed dumps.
        // Keep the actual path in the checklist so callers can locate the file we checked.
        var paths = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true
            }).Select(path =>
            {
                token.ThrowIfCancellationRequested();
                return path;
            }).ToArray()
            : [];
        var digests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Digest(string path)
        {
            if (!digests.TryGetValue(path, out var digest))
            {
                digest = BiosMd5(path, token);
                digests.Add(path, digest);
            }

            return digest;
        }

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

                if (!present && !file.Directory)
                {
                    // A matching name is useful for unhashed keys/firmware too. Never guess a
                    // renamed file without a catalogue checksum.
                    var candidates = paths.Where(candidate => Path.GetFileName(candidate).Equals(
                        Path.GetFileName(file.Path), StringComparison.OrdinalIgnoreCase)
                        || file.Aliases.Contains(Path.GetFileName(candidate), StringComparer.OrdinalIgnoreCase));
                    if (file.Md5.Length > 0 && system.Any)
                    {
                        candidates = candidates.Concat(paths.Where(candidate =>
                            Path.GetExtension(candidate).Equals(Path.GetExtension(file.Path),
                                StringComparison.OrdinalIgnoreCase)));
                    }

                    var found = candidates.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(candidate =>
                    {
                        token.ThrowIfCancellationRequested();
                        return file.Md5.Length == 0 || new FileInfo(candidate).Length <= 64 * 1024 * 1024
                            && file.Md5.Contains(Digest(candidate), StringComparer.OrdinalIgnoreCase);
                    });
                    if (found is not null)
                    {
                        path = found;
                        present = true;
                    }
                }

                var md5 = present && !file.Directory && file.Md5.Length > 0 ? Digest(path) : "";
                var status = !present ? "Missing"
                    : file.Md5.Length == 0 ? "Present"
                    : file.Md5.Contains(md5, StringComparer.OrdinalIgnoreCase) ? "Verified" : "Wrong file";
                return new EmulatorBiosFile(Path.GetRelativePath(folder, path).Replace('\\', '/'), status, md5, file.Optional || system.Optional);
            }).ToArray();
            var required = files.Where(file => !file.Optional).ToArray();
            var ready = system.Any
                ? files.Any(file => file.Status is "Verified" or "Present")
                : required.All(file => file.Status is "Verified" or "Present");
            var status = files.Any(file => file.Status == "Wrong file") && !(system.Any && ready)
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
                    + pair.Key + " → " + pair.Value)).ToArray()) { Any = system.Any };
        }).ToArray();
        lock (_stateLock)
        {
            _bios = new EmulatorBiosState(folder, true, systems);
        }

        NotifyChanged();
    }

    private void RelinkBios(CancellationToken token, string systemId = "", string installationId = "",
        HashSet<string>? installFirmware = null, bool installNativeFirmware = true)
    {
        if (systemId.Length > 0 && !BiosDefinitions.Systems.Any(system => system.Id == systemId))
        {
            throw new InvalidOperationException("Choose a supported BIOS system.");
        }

        var folder = BiosFolder;
        Directory.CreateDirectory(folder);
        var failures = new List<string>();
        Admission(token, () =>
        {
            var installations = EmulatorStorage.ReadInstallations(_context.Root).ToArray();
            foreach (var installed in installations.Where(item =>
                         (systemId.Length == 0 || item.Systems.Contains(systemId))
                         && (installationId.Length == 0 || item.Id == installationId)))
            {
                EmulatorPrerequisites.EnsureStopped(installed);
            }

            var checkedBios = ReadBiosState();
            if (checkedBios.Checked && checkedBios.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var system in checkedBios.Systems.Where(system => systemId.Length == 0 || system.Id == systemId))
                {
                    var definitions = BiosDefinitions.Systems.First(item => item.Id == system.Id).Files;
                    foreach (var file in system.Files.Where(file => file.Status is "Verified" or "Present"))
                    {
                        token.ThrowIfCancellationRequested();
                        var definition = definitions.FirstOrDefault(item => !item.Directory &&
                            (file.Md5.Length > 0
                                ? item.Md5.Contains(file.Md5, StringComparer.OrdinalIgnoreCase)
                                : Path.GetFileName(item.Path).Equals(Path.GetFileName(file.Path),
                                    StringComparison.OrdinalIgnoreCase)
                                  || item.Aliases.Contains(Path.GetFileName(file.Path), StringComparer.OrdinalIgnoreCase)));
                        if (definition is null)
                        {
                            continue;
                        }

                        var source = Path.Combine(folder, file.Path);
                        var destination = Path.Combine(folder, definition.Path);
                        if (!File.Exists(destination))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                            File.Copy(source, destination, overwrite: false);
                        }
                    }
                }
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

                try
                {
                    var configured = new Dictionary<string, string>(installed.ConfiguredPrerequisites);
                    foreach (var rule in installed.DataPolicy.Prerequisites.Where(rule => !rule.NativeInstaller))
                    {
                        var target = installed.DefinitionId is "dolphin" or "primehack"
                            ? Path.Combine(folder, "GC")
                            : folder;
                        if (installed.DefinitionId is "dolphin" or "primehack")
                        {
                            LinkBiosDirectory(Path.Combine(installed.DataPath, "GC"), target);
                        }
                        else if (installed.DefinitionId is "eden" or "citron" or "suyu")
                        {
                            LinkBiosDirectory(Path.Combine(installed.DataPath, "keys"), target);
                        }
                        else if (installed.DataPolicy.HasCores)
                        {
                            ArrangeCoreBios(installed, folder, token);
                            IniFile.SetValue(Path.Combine(installed.DataPath, installed.DataPolicy.ConfigFile),
                                "system_directory", '"' + target.Replace('\\', '/') + '"');
                            LinkBiosDirectory(Path.Combine(installed.DataPath, "system"), target);
                        }
                        else if (rule.IniFile.Length > 0)
                        {
                            IniFile.SetValue(Path.Combine(installed.DataPath, rule.IniFile), rule.IniKey, target,
                                rule.IniSection);
                        }
                        else if (EmulatorPortableProfiles.Supports(installed.DefinitionId))
                        {
                            // A data folder can contain saves too. Copy local system files through
                            // the normal prerequisite path rather than replacing it with a BIOS link.
                            var systemFolder = installed.Systems.Select(system => Path.Combine(folder, system))
                                .FirstOrDefault(Directory.Exists);
                            if (rule.Destination.Length == 0)
                            {
                                continue;
                            }

                            var files = BiosDefinitions.Systems.Where(system => installed.Systems.Contains(system.Id))
                                .SelectMany(system => system.Files).Where(file => !file.Directory)
                                .Select(file => Path.Combine(folder, file.Path)).Where(File.Exists).ToList();
                            if (rule.Kind == "keys" && File.Exists(Path.Combine(folder, "prod.keys")))
                            {
                                files.AddRange(new[] { "prod.keys", "title.keys" }
                                    .Select(name => Path.Combine(folder, name))
                                    .Where(File.Exists));
                            }

                            if (systemFolder is not null)
                            {
                                installed = EmulatorPrerequisites.Configure(installed, systemFolder, rule.Kind, token);
                            }
                            else if (files.Count > 0)
                            {
                                foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase).Where(file =>
                                             rule.Extensions.Length == 0 || rule.Extensions.Contains(
                                                 Path.GetExtension(file),
                                                 StringComparer.OrdinalIgnoreCase)))
                                {
                                    installed = EmulatorPrerequisites.Configure(installed, file, rule.Kind, token);
                                }
                            }
                            else
                            {
                                continue;
                            }

                            target = EmulatorStorage.PrerequisitePath(installed, rule);
                        }

                        configured[rule.Kind] = target;
                    }

                    installed = installed with { ConfiguredPrerequisites = configured };
                    foreach (var rule in installed.DataPolicy.Prerequisites.Where(rule => rule.NativeInstaller && installNativeFirmware))
                    {
                        var source = installed.DefinitionId switch
                        {
                            "rpcs3" => Path.Combine(folder, "PS3UPDAT.PUP"),
                            "vita3k" => Path.Combine(folder, "PSP2UPDAT.PUP"),
                            var id when EmulatorFirmware.InstallsSwitchFirmware(id) =>
                                File.Exists(Path.Combine(folder, "switch", "firmware.zip"))
                                    ? Path.Combine(folder, "switch", "firmware.zip")
                                    : Path.Combine(folder, "switch", "firmware"),
                            _ => ""
                        };
                        if ((File.Exists(source) || Directory.Exists(source))
                            && (installed.Systems.Any(system => installFirmware?.Contains(system) == true)
                                || !EmulatorStorage.PrerequisitePresent(installed, rule))
                            && (installed.DefinitionId != "eden" || File.Exists(Path.Combine(folder, "prod.keys"))))
                        {
                            installed = EmulatorPrerequisites.Configure(installed, source, rule.Kind, token);
                        }
                    }

                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                                 or InvalidOperationException)
                {
                    failures.Add(installed.Name + ": " + error.Message);
                }

                installations[index] = EmulatorPrerequisites.RefreshState(installed);
            }

            Mutate(store => store with { Installations = installations });
        });
        if (failures.Count > 0)
        {
            throw new InvalidOperationException("Some emulators could not be linked: " + string.Join("; ", failures));
        }
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

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static BiosCatalog LoadBiosCatalog()
    {
        using var stream =
            typeof(EmulatorManager).Assembly.GetManifestResourceStream("WSGM.Core.Emulators.bios-catalog.json")!;
        return JsonSerializer.Deserialize<BiosCatalog>(stream, EmulatorStorage.JsonOptions)!;
    }
}
