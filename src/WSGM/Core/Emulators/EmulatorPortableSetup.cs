using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace WSGM.Core;

/// <summary>Creates each reviewed portable layout and preserves data before owned program replacement.</summary>
internal static class EmulatorPortableSetup
{
    internal static void CopyData(string source, string destination, CancellationToken token,
        bool packagePresent = false,
        IReadOnlySet<string>? excludedNames = null, bool packageResources = false, bool overwriteExisting = true,
        IReadOnlySet<string>? excludedLinks = null, bool requiredSource = false)
    {
        if (!Directory.Exists(source))
        {
            if (requiredSource)
            {
                throw new InvalidDataException("The configured emulator data directory is unavailable: " + source
                    + ". Reconnect or restore it before converting to portable storage.");
            }

            if (new DirectoryInfo(source).LinkTarget is not null)
            {
                _ = EmulatorPortable.PhysicalPath(source);
            }

            return;
        }

        var sourceRoot = EmulatorPortable.PhysicalPath(source);
        Copy(source, destination, packageResources, new HashSet<string>(StringComparer.OrdinalIgnoreCase), true);

        void Copy(string from, string to, bool resources, HashSet<string> ancestors, bool first)
        {
            var actual = EmulatorPortable.PhysicalPath(from);
            if (!ancestors.Add(actual))
            {
                throw new InvalidDataException("The portable user data contains a recursive directory link: " + from);
            }

            try
            {
                Directory.CreateDirectory(to);
                foreach (var entry in Directory.EnumerateFileSystemEntries(from))
                {
                    token.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(entry);
                    if (name == ".packages" || name.StartsWith(".portable-snapshot", StringComparison.Ordinal)
                                            || (first && excludedNames?.Contains(name) == true)
                                            || excludedLinks?.Contains(Path.GetFullPath(entry)) == true)
                    {
                        continue;
                    }

                    var attributes = File.GetAttributes(entry);
                    var linked = (attributes & FileAttributes.ReparsePoint) != 0;
                    var original = linked ? EmulatorPortable.PhysicalPath(entry) : entry;
                    var target = Path.Combine(to, name);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (linked && StoragePaths.IsUnder(original, sourceRoot))
                        {
                            throw new InvalidDataException(
                                "The portable user data contains a recursive directory link: " + entry);
                        }

                        if (StoragePaths.IsUnder(entry, destination) && !EmulatorPortable.Same(entry, target))
                        {
                            continue;
                        }

                        var targetInfo = new DirectoryInfo(target);
                        if (targetInfo.LinkTarget is not null)
                        {
                            var retained = EmulatorPortable.PhysicalPath(target);
                            var temporary = target + ".portable-copy-" + Guid.NewGuid().ToString("N");
                            Copy(retained, temporary, resources,
                                new HashSet<string>(ancestors, StringComparer.OrdinalIgnoreCase), false);
                            Directory.Delete(target); // Unlink only; the original user directory remains intact.
                            Directory.Move(temporary, target);
                            if (EmulatorPortable.Same(entry, target))
                            {
                                continue;
                            }
                        }

                        Copy(original, target,
                            resources ||
                            (packagePresent && name.Equals("resources", StringComparison.OrdinalIgnoreCase)),
                            new HashSet<string>(ancestors, StringComparer.OrdinalIgnoreCase), false);
                        continue;
                    }

                    if (packagePresent && Path.GetExtension(name).ToLowerInvariant() is ".exe" or ".dll" or ".pdb")
                    {
                        continue;
                    }

                    if ((resources || !overwriteExisting) && File.Exists(target))
                    {
                        continue;
                    }

                    var targetFile = new FileInfo(target);
                    if (targetFile.LinkTarget is not null)
                    {
                        var retained = EmulatorPortable.PhysicalPath(target);
                        File.Copy(retained, target + ".before-portable-" + Guid.NewGuid().ToString("N"), false);
                        File.Delete(target); // Remove the file alias, not the file it named.
                    }
                    else if (EmulatorPortable.Same(entry, target))
                    {
                        continue;
                    }

                    if (File.Exists(target))
                    {
                        bool identical;
                        using (var input = File.OpenRead(original))
                        using (var current = File.OpenRead(target))
                        {
                            identical = SHA256.HashData(input).AsSpan().SequenceEqual(SHA256.HashData(current));
                        }

                        if (identical)
                        {
                            continue;
                        }

                        File.Copy(target, target + ".before-portable-" + Guid.NewGuid().ToString("N"), false);
                    }

                    File.Copy(original, target, true);
                }
            }
            finally
            {
                ancestors.Remove(actual);
            }
        }
    }

    internal static EmulatorInstallation Prepare(EmulatorInstallation installed, string retainedData,
        EmulatorInstallation? previous, CancellationToken token, bool staged,
        Func<IReadOnlyList<string>>? knownRomPaths = null, Action<string, string>? createAlias = null)
    {
        if (!installed.Managed)
        {
            return installed;
        }

        var program = Path.GetDirectoryName(installed.ExecutablePath)!;
        if (!StoragePaths.IsUnder(installed.Root, program))
        {
            throw new InvalidDataException("The managed emulator executable is outside its owned installation root.");
        }

        // A disconnected explicit data redirect must not become an empty portable folder.
        // Check every source before changing any configuration or native portable binding.
        ValidateDataSources(installed, retainedData, previous, token);

        // Resolve removable ROM media before touching the old configuration or its portable aliases.
        var romPaths = installed.DefinitionId == "retroarch"
            ? RetroArchPortableData.ReadRomPaths(previous ?? installed, knownRomPaths, token)
            : [];
        var gbaSaves = installed.DefinitionId == "dolphin"
            ? DolphinPortableData.ReadGbaSaves(previous ?? installed).ToArray()
            : [];

        if (previous is not null)
        {
            EmulatorPrerequisites.EnsureStopped(previous);
            CopyData(previous.DataPath, retainedData, token, requiredSource: true);
        }

        Directory.CreateDirectory(retainedData);
        CopyData(retainedData, retainedData, token);
        installed = installed with { DataPath = retainedData, OwnsData = true };
        switch (installed.DefinitionId)
        {
            case "duckstation":
                if (!staged)
                {
                    EmulatorPrerequisites.EnsureStopped(installed);
                }

                CopyData(retainedData, program, token, true);
                AtomicFile.WriteText(Path.Combine(program, "portable.txt"), "", true);
                installed = installed with { DataPath = program };
                break;
            case "eden" or "rpcs3":
                Link(program, EmulatorPortableProfiles.Alias(installed.DefinitionId), retainedData, token, staged,
                    createAlias);
                break;
            case "dolphin":
                Link(program, EmulatorPortableProfiles.Alias(installed.DefinitionId), retainedData, token, staged,
                    createAlias);
                AtomicFile.WriteText(Path.Combine(program, "portable.txt"), "", true);
                break;
            case "pcsx2": Pcsx2Data.Prepare(installed, staged: staged); break;
            case "retroarch": break;
            default:
                if (!EmulatorPortableProfiles.Supports(installed.DefinitionId))
                {
                    throw new InvalidDataException("This emulator has no reviewed portable layout.");
                }

                var alias = EmulatorPortableProfiles.Alias(installed.DefinitionId);
                if (alias.Length > 0)
                {
                    Link(program, alias, retainedData, token, staged, createAlias);
                }
                else
                {
                    CopyData(retainedData, program, token, packagePresent: true);
                    installed = installed with { DataPath = program };
                }

                AdditionalEmulatorSetup.RebaseOwnedPaths(installed, previous, token);
                AdditionalEmulatorSetup.Configure(installed);
                break;
        }

        if (installed.DefinitionId == "retroarch")
        {
            RetroArchPortableData.PrepareConfigs(installed, previous, token, romPaths);
        }

        foreach (var (file, section, key, folder) in EmulatorPortable.DataFolders(installed.DefinitionId))
        {
            var config = Path.Combine(installed.DataPath, file);
            var value = IniFile.ReadValue(config, key, section)?.Trim('"');
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (EmulatorPortable.IsUnder(installed.DataPath,
                    EmulatorPortable.ResolveSettingPath(installed, section, key, value)))
            {
                continue;
            }

            var source = EmulatorPortable.ResolveSettingPath(previous ?? installed, section, key, value);
            var destination = Path.Combine(installed.DataPath, folder);
            if (File.Exists(source))
            {
                destination = Path.Combine(destination, Path.GetFileName(source));
            }

            CopyPath(source, destination, token,
                !EmulatorPortable.IsUnder((previous ?? installed).DataPath, source));
            // Preserve the source and every collision before changing the native directory setting.
            var nativePath = installed.DefinitionId is "eden" or "citron" or "suyu" or "dolphin" or "primehack" ||
                             (installed.DefinitionId == "duckstation" && section == "MemoryCards" && key != "Directory")
                ? destination
                : Path.GetRelativePath(installed.DataPath, destination);
            IniFile.SetValue(config, key, nativePath.Replace('\\', '/'), section);
        }

        if (installed.DefinitionId == "rpcs3")
        {
            var redirected = EmulatorPortable.RpcPaths(installed.DataPath)
                .Where(path => !EmulatorPortable.IsUnder(installed.DataPath, path.Path)).ToArray();
            if (redirected.Length > 0)
            {
                var file = Path.Combine(installed.DataPath, "vfs.yml");
                var lines = File.ReadAllLines(file);
                var root = redirected.FirstOrDefault(path => path.Key == "$(EmulatorDir)").Path
                           ?? installed.DataPath;
                foreach (var (key, source, folder) in redirected)
                {
                    var required = folder.Length == 0 || !EmulatorPortable.Same(source, Path.Combine(root, folder));
                    if (required)
                    {
                        RequireDataSource(source);
                    }

                    CopyData(source, Path.Combine(installed.DataPath, folder), token, requiredSource: required);
                    for (var index = 0; index < lines.Length; index++)
                    {
                        var colon = lines[index].IndexOf(':');
                        if (colon > 0 && lines[index][..colon].Trim().Trim('"', '\'') == key)
                        {
                            lines[index] = lines[index][..(colon + 1)] + " \"" +
                                           (folder.Length == 0 ? "" : "$(EmulatorDir)" + folder + "/") + "\"";
                        }
                    }
                }

                File.Copy(file, file + ".before-portable-" + Guid.NewGuid().ToString("N"));
                AtomicFile.WriteText(file, string.Join(Environment.NewLine, lines) + Environment.NewLine, true);
            }
        }

        if (installed.DefinitionId == "dolphin")
        {
            DolphinPortableData.PreserveGbaSaves(installed, gbaSaves, token);
        }

        return installed;
    }

    private static void ValidateDataSources(EmulatorInstallation installed, string retainedData,
        EmulatorInstallation? previous, CancellationToken token)
    {
        if (previous is not null)
        {
            RequireDataSource(previous.DataPath);
        }

        var sources = new List<EmulatorInstallation>
        {
            previous ?? installed, installed with { DataPath = retainedData }
        };
        var alias = installed.DefinitionId is "duckstation" or "eden" or "rpcs3" or "dolphin" ||
                    EmulatorPortableProfiles.Supports(installed.DefinitionId)
            ? EmulatorPortableProfiles.Alias(installed.DefinitionId)
            : null;
        if (alias is not null)
        {
            var native = Path.Combine(Path.GetDirectoryName(installed.ExecutablePath)!, alias);
            _ = EmulatorPortable.PhysicalPath(native);
            if (Directory.Exists(native))
            {
                sources.Add(installed with { DataPath = native });
            }
        }

        foreach (var source in sources.DistinctBy(item => item.DataPath, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            foreach (var (file, section, key, _) in EmulatorPortable.DataFolders(source.DefinitionId))
            {
                var text = ReadOptionalConfig(Path.Combine(source.DataPath, file));
                var value = text is null ? null : IniFile.ReadTextValue(text, key, section)?.Trim('"');
                if (!string.IsNullOrWhiteSpace(value))
                {
                    RequireRedirect(EmulatorPortable.ResolveSettingPath(source, section, key, value));
                }
            }

            if (source.DefinitionId == "rpcs3")
            {
                var paths = EmulatorPortable.RpcPaths(source.DataPath).ToArray();
                var root = paths.FirstOrDefault(path => path.Key == "$(EmulatorDir)").Path ?? source.DataPath;
                foreach (var (_, path, folder) in paths)
                {
                    if (folder.Length == 0 || !EmulatorPortable.Same(path, Path.Combine(root, folder)))
                    {
                        RequireRedirect(path);
                    }
                }
            }

            if (source.DefinitionId == "retroarch")
            {
                var main = Path.Combine(source.DataPath, "retroarch.cfg");
                var text = ReadOptionalConfig(main);
                var setting = text is null ? null : IniFile.ReadTextValue(text, "rgui_config_directory")?.Trim('"');
                var directory = string.IsNullOrWhiteSpace(setting) || setting == "default"
                    ? Path.Combine(source.DataPath, "config")
                    : Path.GetFullPath(Path.Combine(source.DataPath, setting));
                RequireRedirect(directory);
                var configs = new[] { main }.Concat(Directory.Exists(directory)
                    ? EmulatorPortable.ConfigFiles(directory)
                    : []);
                foreach (var config in configs)
                {
                    text = ReadOptionalConfig(config);
                    foreach (var key in EmulatorPortable.RetroArchDataPaths)
                    {
                        var value = text is null ? null : IniFile.ReadTextValue(text, key)?.Trim('"');
                        if (!string.IsNullOrWhiteSpace(value) && value != "default")
                        {
                            RequireRedirect(Path.GetFullPath(Path.Combine(source.DataPath, value)));
                        }
                    }
                }
            }

            void RequireRedirect(string path)
            {
                if (!EmulatorPortable.IsUnder(source.DataPath, path))
                {
                    RequireDataSource(path);
                }
            }
        }
    }

    private static string? ReadOptionalConfig(string path)
    {
        _ = EmulatorPortable.PhysicalPath(path);
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception failure) when (failure is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void RequireDataSource(string path)
    {
        try
        {
            _ = EmulatorPortable.PhysicalPath(path);
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            {
                using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                _ = entries.MoveNext();
            }
            else
            {
                using var data = File.OpenRead(path);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The configured emulator data source is unavailable: " + path
                + ". Reconnect or restore it before converting to portable storage. The existing configuration and binding are preserved.",
                failure);
        }
    }

    /// <summary>Refuses a conversion while the media holding a referenced ROM is disconnected.</summary>
    /// <param name="rom">ROM file or folder path.</param>
    internal static void RequireRomMedia(string rom)
    {
        if (!File.Exists(rom) && !Directory.Exists(rom))
        {
            throw new InvalidDataException("Reconnect the ROM media for " + rom
                                                                          + " before converting content-directory data to portable storage. The active version is preserved.");
        }
    }

    /// <summary>Copies one file or folder into portable data, backing up a file it replaces.</summary>
    /// <param name="source">File or folder to copy.</param>
    /// <param name="destination">Destination path.</param>
    /// <param name="token">Cancels the copy.</param>
    /// <param name="required">Whether a missing source is a failure.</param>
    internal static void CopyPath(string source, string destination, CancellationToken token, bool required = false)
    {
        if (required)
        {
            RequireDataSource(source);
        }

        if (EmulatorPortable.Same(source, destination))
        {
            return;
        }

        if (Directory.Exists(source))
        {
            CopyData(source, destination, token, requiredSource: required);
        }
        else if (File.Exists(source))
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination))
            {
                File.Copy(destination, destination + ".before-portable-" + Guid.NewGuid().ToString("N"));
            }

            File.Copy(source, destination, true);
        }
        else if (required)
        {
            throw new InvalidDataException("The configured emulator data source became unavailable: " + source
                + ". Reconnect or restore it before converting to portable storage.");
        }
    }

    private static void Link(string program, string name, string data, CancellationToken token, bool staged,
        Action<string, string>? createAlias)
    {
        var link = Path.Combine(program, name);
        var directory = new DirectoryInfo(link);
        if (directory.LinkTarget is not null && directory.Exists
                                             && EmulatorPortable.Same(directory.ResolveLinkTarget(true)!.FullName, data))
        {
            return;
        }

        var replacement = link + ".portable-link-" + Guid.NewGuid().ToString("N");
        string? backup = null;
        try
        {
            // A failed symlink/junction creation must never remove the active portable binding.
            // Keep its exact reparse point for rollback, including relative symbolic-link targets.
            (createAlias ?? CreateAlias)(replacement, data);
            if (directory.LinkTarget is not null || directory.Exists)
            {
                CopyData(EmulatorPortable.PhysicalPath(link), data, token, overwriteExisting: !staged,
                    requiredSource: true);
                backup = link + ".before-portable-" + Guid.NewGuid().ToString("N");
                Directory.Move(link, backup);
            }

            Directory.Move(replacement, link);
        }
        catch
        {
            if (backup is not null && !Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is null)
            {
                Directory.Move(backup, link);
            }

            throw;
        }
        finally
        {
            if (new DirectoryInfo(replacement).LinkTarget is not null)
            {
                Directory.Delete(replacement); // Unlink only; never remove the retained data.
            }
        }
    }

    internal static void CreateAlias(string link, string data)
    {
        try
        {
            Directory.CreateSymbolicLink(link, data);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            if (link.IndexOfAny(['"', '%']) >= 0 || data.IndexOfAny(['"', '%']) >= 0)
            {
                throw;
            }

            var start = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/d /c mklink /J \"{link}\" \"{data}\"", CreateNoWindow = true,
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (!ThemePaths.RunJunctionCommand(start, TimeSpan.FromSeconds(10)))
            {
                throw new IOException("The portable " + Path.GetFileName(link) + " folder could not be linked.",
                    failure);
            }
        }
    }

    internal static void DeleteProgram(string path, string root)
    {
        var full = Path.GetFullPath(path);
        if (!StoragePaths.IsUnder(root, full) || EmulatorPortable.Same(full, root))
        {
            throw new IOException("The portable cleanup target is outside its owned program root.");
        }

        if (!Directory.Exists(full))
        {
            return;
        }

        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(full);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(full))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(entry);
                }
                else
                {
                    DeleteProgram(entry, root);
                }
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(full);
    }

    internal static void PreserveVersion(EmulatorInstallation installed, string version, string retainedData,
        CancellationToken token)
    {
        if (!installed.Managed || !StoragePaths.IsUnder(installed.Root, version))
        {
            throw new InvalidDataException("The retired emulator version is outside its owned root.");
        }

        // Skip only the native version aliases whose targets are already retained separately.
        // DuckStation writes beside its executable, including atomically replaced settings.ini.
        var destination = Path.Combine(retainedData, ".portable-snapshots", Path.GetFileName(version));
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var name = EmulatorPortableProfiles.Alias(installed.DefinitionId);
        var pending = new Stack<string>();
        pending.Push(version);
        while (pending.Count > 0)
        {
            foreach (var directory in Directory.EnumerateDirectories(pending.Pop()))
            {
                var info = new DirectoryInfo(directory);
                if (info.LinkTarget is null)
                {
                    pending.Push(directory);
                }
                else if (name.Length > 0 && info.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                         && File.Exists(Path.Combine(info.Parent!.FullName,
                                             Path.GetFileName(installed.ExecutablePath)))
                                         && EmulatorPortable.Same(EmulatorPortable.PhysicalPath(directory),
                                             EmulatorPortable.PhysicalPath(retainedData)))
                {
                    aliases.Add(Path.GetFullPath(directory));
                }
            }
        }

        CopyData(version, destination, token, excludedLinks: aliases);
    }
}
