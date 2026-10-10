using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace WSGM.Core;

/// <summary>Creates each reviewed portable layout and preserves data before owned program replacement.</summary>
internal static class EmulatorPortableSetup
{
    private static readonly string[] RetroArchSorting =
    [
        "sort_savefiles_enable", "sort_savestates_enable", "sort_savefiles_by_content_enable",
        "sort_savestates_by_content_enable"
    ];

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

                        if (StoragePaths.IsUnder(entry, destination) && !Same(entry, target))
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
                            if (Same(entry, target))
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
                    else if (Same(entry, target))
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
        Func<IReadOnlyList<string>>? knownRomPaths = null)
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
            ? ReadRetroArchRomPaths(previous ?? installed, knownRomPaths, token)
            : [];
        var gbaSaves = installed.DefinitionId == "dolphin"
            ? DolphinGbaSaves(previous ?? installed).ToArray()
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
            case "eden": Link(program, "user", retainedData, token, staged); break;
            case "rpcs3": Link(program, "portable", retainedData, token, staged); break;
            case "dolphin":
                Link(program, "User", retainedData, token, staged);
                AtomicFile.WriteText(Path.Combine(program, "portable.txt"), "", true);
                break;
            case "pcsx2": Pcsx2Data.Prepare(installed, staged: staged); break;
            case "retroarch": break;
            default: throw new InvalidDataException("This emulator has no reviewed portable layout.");
        }

        if (installed.DefinitionId == "retroarch")
        {
            PrepareRetroArchConfigs(installed, previous, token, romPaths);
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
            var nativePath = installed.DefinitionId is "eden" or "dolphin" ||
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
                    var required = folder.Length == 0 || !Same(source, Path.Combine(root, folder));
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
            PreserveDolphinGbaSaves(installed, gbaSaves, token);
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
        var alias = installed.DefinitionId switch
        {
            "duckstation" => "", "eden" => "user", "rpcs3" => "portable", "dolphin" => "User", _ => null
        };
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
                    if (folder.Length == 0 || !Same(path, Path.Combine(root, folder)))
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

    private static IReadOnlyCollection<string> ReadRetroArchRomPaths(EmulatorInstallation previous,
        Func<IReadOnlyList<string>>? knownRomPaths, CancellationToken token)
    {
        var main = Path.Combine(previous.DataPath, "retroarch.cfg");
        if (!File.Exists(main))
        {
            return [];
        }

        var configs = new List<string> { main };
        var overrideSetting = IniFile.ReadValue(main, "rgui_config_directory")?.Trim('"');
        var overrides = string.IsNullOrWhiteSpace(overrideSetting) || overrideSetting == "default"
            ? Path.Combine(previous.DataPath, "config")
            : Path.GetFullPath(Path.Combine(previous.DataPath, overrideSetting));
        if (Directory.Exists(overrides))
        {
            configs.AddRange(EmulatorPortable.ConfigFiles(overrides));
        }

        if (!configs.Any(config => EmulatorPortable.RetroArchFlags.Any(key =>
                EmulatorPortable.IsTrue(IniFile.ReadValue(config, key)))))
        {
            return [];
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var program = Path.GetDirectoryName(previous.ExecutablePath)!;
        foreach (var path in knownRomPaths?.Invoke() ?? [])
        {
            AddRom(path);
        }

        var playlists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in configs)
        {
            foreach (var key in EmulatorPortable.RetroArchDataPaths.Where(key =>
                         key.StartsWith("content_", StringComparison.Ordinal)))
            {
                var value = IniFile.ReadValue(config, key)?.Trim('"');
                if (!string.IsNullOrWhiteSpace(value) && value != "default")
                {
                    playlists.Add(Path.GetFullPath(Path.Combine(previous.DataPath, value)));
                }
                else if (Same(config, main))
                {
                    playlists.Add(Path.Combine(previous.DataPath, key[..^5] + ".lpl"));
                }
            }

            var setting = IniFile.ReadValue(config, "playlist_directory")?.Trim('"');
            var directory = string.IsNullOrWhiteSpace(setting) || setting == "default"
                ? Same(config, main) ? Path.Combine(previous.DataPath, "playlists") : null
                : Path.GetFullPath(Path.Combine(previous.DataPath, setting));
            if (directory is not null && Directory.Exists(directory))
            {
                foreach (var file in EmulatorPortable.ConfigFiles(directory, "*.lpl"))
                {
                    playlists.Add(file);
                }
            }
        }

        foreach (var file in playlists.Where(File.Exists))
        {
            token.ThrowIfCancellationRequested();
            var text = File.ReadAllText(file);
            if (!text.TrimStart().StartsWith('{'))
            {
                // RetroArch also accepts its original six-line playlist format.
                var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                for (var index = 0; index + 5 < lines.Length; index += 6)
                {
                    AddRom(lines[index]);
                }

                continue;
            }

            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String &&
                        path.GetString() is { Length: > 0 } rom)
                    {
                        AddRom(rom);
                    }
                }
            }
        }

        foreach (var rom in paths)
        {
            token.ThrowIfCancellationRequested();
            var archive = rom.IndexOf('#');
            RequireRomMedia(archive < 0 ? rom : rom[..archive]);
        }

        var mainFlags = RetroArchFlags(main, null);
        foreach (var config in configs)
        {
            foreach (var rom in paths)
            {
                var flags = Same(config, main) ? mainFlags : RetroArchFlags(config, mainFlags, rom);
                foreach (var key in new[] { "savefile_directory", "savestate_directory" })
                {
                    var saves = key == "savefile_directory";
                    if (!flags[saves ? "savefiles_in_content_dir" : "savestates_in_content_dir"]
                        || !flags[saves ? "sort_savefiles_enable" : "sort_savestates_enable"])
                    {
                        continue;
                    }

                    var (parent, stem) = RetroArchContentName(rom);

                    bool Matches(string file)
                    {
                        return RetroArchSidecarKey(previous, Path.GetFileName(file)[stem.Length..]) == key;
                    }

                    if (!Directory.EnumerateFiles(parent, stem + ".*").Any(Matches))
                    {
                        continue;
                    }

                    var sorted = RetroArchSidecarDirectories(parent, flags, saves)
                        .Where(directory => directory.Relative.Length > 0);
                    if (!sorted.Any(directory => Directory.EnumerateFiles(directory.Source, stem + ".*").Any(Matches)))
                    {
                        throw new InvalidDataException("RetroArch cannot safely bind the flat "
                                                       + (saves ? "save" : "savestate") + " for " + rom
                                                       + " while core sorting is enabled. Restore its native core save folder before converting; the original configuration is preserved.");
                    }
                }
            }
        }

        return paths;

        void AddRom(string rom)
        {
            if (string.IsNullOrWhiteSpace(rom) || rom.Contains("://", StringComparison.Ordinal))
            {
                return;
            }

            var archive = rom.IndexOf('#');
            paths.Add(Path.GetFullPath(Path.Combine(program, archive < 0 ? rom : rom[..archive]))
                      + (archive < 0 ? "" : rom[archive..]));
        }
    }

    private static void PrepareRetroArchConfigs(EmulatorInstallation installed, EmulatorInstallation? previous,
        CancellationToken token, IReadOnlyCollection<string> knownRomPaths)
    {
        var main = Path.Combine(installed.DataPath, "retroarch.cfg");
        var mainFlags = RetroArchFlags(main, null);
        var originalValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Import the original override directory before looking for per-core and per-game files.
        Rewrite(main, installed.DataPath, mainFlags);
        var configured = IniFile.ReadValue(main, "rgui_config_directory")?.Trim('"');
        var directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(installed.DataPath, "config")
            : Path.GetFullPath(Path.Combine(installed.DataPath, configured));
        if (!Directory.Exists(directory))
        {
            return;
        }

        var configs = EmulatorPortable.ConfigFiles(directory).ToArray();
        foreach (var config in configs)
        {
            originalValues[config] = File.ReadAllText(config);
        }

        var originalFlags = configs.ToDictionary(config => config,
            config => RetroArchFlags(config, mainFlags, originalValues: originalValues),
            StringComparer.OrdinalIgnoreCase);
        foreach (var config in configs)
        {
            foreach (var rom in knownRomPaths)
            {
                var inherited = RetroArchFlags(config, mainFlags, rom, originalValues);
                foreach (var key in EmulatorPortable.RetroArchFlags)
                {
                    originalFlags[config][key] |= inherited[key];
                }
            }
        }

        foreach (var config in configs)
        {
            var relative = Path.ChangeExtension(Path.GetRelativePath(directory, config), null);
            Rewrite(config, Path.Combine(installed.DataPath, "override-data", relative), originalFlags[config]);
        }

        void Rewrite(string config, string overrideData, IReadOnlyDictionary<string, bool>? original = null)
        {
            var mainConfig = Same(config, main);
            var flags = original ?? RetroArchFlags(config, mainFlags);
            var destinations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in EmulatorPortable.RetroArchDataPaths)
            {
                var value = IniFile.ReadValue(config, key)?.Trim('"');
                if (string.IsNullOrWhiteSpace(value) ||
                    !installed.DataPolicy.ConfigPaths.TryGetValue(key, out var template))
                {
                    continue;
                }

                if (value != "default" && !mainConfig && EmulatorPortable.IsUnder(installed.DataPath,
                        Path.GetFullPath(Path.Combine(installed.DataPath, value))))
                {
                    destinations[key] = Path.GetFullPath(Path.Combine(installed.DataPath, value));
                    continue;
                }

                var source = value == "default"
                    ? null
                    : Path.GetFullPath(Path.Combine(previous?.DataPath ?? installed.DataPath, value));
                var destination = template.Replace("{data}", overrideData, StringComparison.Ordinal);
                if (!mainConfig && source is not null && previous is not null &&
                    EmulatorPortable.IsUnder(previous.DataPath, source))
                {
                    destination = Path.Combine(installed.DataPath, Path.GetRelativePath(previous.DataPath, source));
                }

                destinations[key] = destination;
                if (source is not null)
                {
                    CopyPath(source, destination, token,
                        !EmulatorPortable.IsUnder(previous?.DataPath ?? installed.DataPath, source));
                }
            }

            foreach (var (key, flag) in new[]
                     {
                         ("savefile_directory", "savefiles_in_content_dir"),
                         ("savestate_directory", "savestates_in_content_dir"),
                         ("screenshot_directory", "screenshots_in_content_dir"),
                         ("system_directory", "systemfiles_in_content_dir")
                     })
            {
                if ((mainConfig || flags[flag])
                    && !destinations.ContainsKey(key) &&
                    installed.DataPolicy.ConfigPaths.TryGetValue(key, out var template))
                {
                    destinations[key] = template.Replace("{data}", overrideData, StringComparison.Ordinal);
                }
            }

            PreserveRetroArchSidecars(installed, previous, flags, destinations, knownRomPaths, token,
                mainConfig ? null : rom => RetroArchFlags(config, mainFlags, rom, originalValues));
            foreach (var (key, destination) in destinations)
            {
                IniFile.SetValue(config, key, '"' + destination.Replace('\\', '/') + '"');
            }

            foreach (var key in EmulatorPortable.RetroArchFlags)
            {
                if (flags[key])
                {
                    IniFile.SetValue(config, key, "false");
                }
            }
        }
    }

    private static Dictionary<string, bool> RetroArchFlags(string config, IReadOnlyDictionary<string, bool>? inherited,
        string? rom = null, IReadOnlyDictionary<string, string>? originalValues = null)
    {
        if (inherited is not null)
        {
            var directory = Path.GetDirectoryName(config)!;
            var core = Path.Combine(directory, Path.GetFileName(directory) + ".cfg");
            if (!Same(core, config) && File.Exists(core))
            {
                inherited = RetroArchFlags(core, inherited, originalValues: originalValues);
            }

            if (rom is not null)
            {
                var (parent, stem) = RetroArchContentName(rom);
                var content = Path.Combine(directory, Path.GetFileName(parent) + ".cfg");
                if (Path.GetFileNameWithoutExtension(config).Equals(stem, StringComparison.OrdinalIgnoreCase)
                    && !Same(content, config) && !Same(content, core) && File.Exists(content))
                {
                    inherited = RetroArchFlags(content, inherited, originalValues: originalValues);
                }
            }
        }

        var text = originalValues?.GetValueOrDefault(config);
        return EmulatorPortable.RetroArchFlags.Concat(RetroArchSorting).ToDictionary(key => key, key =>
                (text is null ? IniFile.ReadValue(config, key) : IniFile.ReadTextValue(text, key)) is { } value
                    ? EmulatorPortable.IsTrue(value)
                    : inherited?.GetValueOrDefault(key) ?? key is "sort_savefiles_enable" or "sort_savestates_enable",
            StringComparer.Ordinal);
    }

    private static (string Parent, string Stem) RetroArchContentName(string rom)
    {
        var archive = rom.IndexOf('#');
        var parent = Path.GetDirectoryName(Path.GetFullPath(archive < 0 ? rom : rom[..archive]));
        var stem = Path.GetFileNameWithoutExtension(archive < 0 ? rom : rom[(archive + 1)..]);
        return (parent ?? throw new InvalidDataException("The ROM has no parent directory: " + rom), stem);
    }

    private static IEnumerable<(string Source, string Relative)> RetroArchSidecarDirectories(string parent,
        IReadOnlyDictionary<string, bool> flags, bool saves)
    {
        // runloop_path_set_redirect appends the content-folder name first, then the runtime library_name.
        // Keep actual native folder names: catalogue/history display names need not match library_name.
        var prefix = flags[saves ? "sort_savefiles_by_content_enable" : "sort_savestates_by_content_enable"]
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(parent))
            : "";
        var intermediate = Path.Combine(parent, prefix);
        if (Directory.Exists(intermediate))
        {
            if (flags[saves ? "sort_savefiles_enable" : "sort_savestates_enable"])
            {
                foreach (var directory in Directory.EnumerateDirectories(intermediate))
                {
                    yield return (directory, Path.Combine(prefix, Path.GetFileName(directory)));
                }
            }
            else if (prefix.Length > 0)
            {
                yield return (intermediate, prefix);
            }
        }

        // Retain the native mkdir-failure fallback too. Ambiguous core-sorted fallbacks fail in preflight.
        yield return (parent, flags[saves ? "sort_savefiles_enable" : "sort_savestates_enable"] ? "" : prefix);
    }

    private static string? RetroArchSidecarKey(EmulatorInstallation installed, string suffix)
    {
        suffix = suffix.ToLowerInvariant();
        if (suffix is ".srm" or ".rtc" || (suffix == ".brm" && installed.Cores.Any(core =>
                core.Id.Contains("clownmdemu", StringComparison.OrdinalIgnoreCase))))
        {
            return "savefile_directory";
        }

        if (suffix is ".state" or ".state.auto" || (suffix.StartsWith(".state", StringComparison.Ordinal)
                                                    && suffix.Length > 6 && suffix[6..].All(char.IsAsciiDigit)))
        {
            return "savestate_directory";
        }

        return suffix == ".png" ? "screenshot_directory" : null;
    }

    private static void PreserveRetroArchSidecars(EmulatorInstallation installed, EmulatorInstallation? previous,
        IReadOnlyDictionary<string, bool> flags, IReadOnlyDictionary<string, string> destinations,
        IReadOnlyCollection<string> paths, CancellationToken token,
        Func<string, IReadOnlyDictionary<string, bool>>? romFlags = null)
    {
        var saves = flags["savefiles_in_content_dir"];
        var states = flags["savestates_in_content_dir"];
        var images = flags["screenshots_in_content_dir"];
        var system = flags["systemfiles_in_content_dir"];
        if (!saves && !states && !images && !system)
        {
            return;
        }

        foreach (var rom in paths)
        {
            token.ThrowIfCancellationRequested();
            var archive = rom.IndexOf('#');
            var (parent, stem) = RetroArchContentName(rom);
            RequireRomMedia(archive < 0 ? rom : rom[..archive]);
            if (system && destinations.TryGetValue("system_directory", out var systemDirectory))
            {
                foreach (var required in installed.Cores.Concat(previous?.Cores ?? [])
                             .SelectMany(core => core.RequiredFiles).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var relative = required;
                    if (Path.IsPathRooted(relative))
                    {
                        var roots = new[]
                        {
                            Path.Combine(installed.DataPath, "system"),
                            Path.Combine(previous?.DataPath ?? installed.DataPath, "system")
                        };
                        var root = roots.FirstOrDefault(path => StoragePaths.IsUnder(path, required));
                        if (root is null)
                        {
                            continue;
                        }

                        relative = Path.GetRelativePath(root, required);
                    }

                    var source = Path.GetFullPath(Path.Combine(parent, relative));
                    if (!EmulatorPortable.IsUnder(parent, source))
                    {
                        continue;
                    }

                    if (Directory.Exists(source) && EmulatorPortable.ConfigFiles(source, "*")
                            .Any(file => !EmulatorPortable.IsUnder(parent, file)))
                    {
                        throw new InvalidDataException(
                            "The declared ROM firmware contains a link outside its content directory: " + source);
                    }

                    CopyPath(source, Path.Combine(systemDirectory, relative), token);
                }
            }

            foreach (var key in new[] { "savefile_directory", "savestate_directory", "screenshot_directory" })
            {
                if ((key == "savefile_directory" && !saves) || (key == "savestate_directory" && !states)
                                                            || (key == "screenshot_directory" && !images) ||
                                                            !destinations.TryGetValue(key, out var destination))
                {
                    continue;
                }

                var directories = key == "screenshot_directory"
                    ? new[] { (Source: parent, Relative: "") }
                    : RetroArchSidecarDirectories(parent, romFlags?.Invoke(rom) ?? flags, key == "savefile_directory");
                foreach (var (source, relative) in directories)
                {
                    foreach (var file in Directory.EnumerateFiles(source, stem + ".*", SearchOption.TopDirectoryOnly))
                    {
                        if (RetroArchSidecarKey(installed, Path.GetFileName(file)[stem.Length..]) != key)
                        {
                            continue;
                        }

                        var target = Path.Combine(destination, relative, Path.GetFileName(file));
                        CopyPath(file, target, token);
                        if (key == "savestate_directory" && File.Exists(file + ".png"))
                        {
                            CopyPath(file + ".png", target + ".png", token);
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<string> DolphinGbaSaves(EmulatorInstallation installed)
    {
        var config = Path.Combine(installed.DataPath, "Config", "Dolphin.ini");
        if (!EmulatorPortable.IsTrue(IniFile.ReadValue(config, "SavesInRomPath", "GBA")))
        {
            yield break;
        }

        var program = Path.GetDirectoryName(installed.ExecutablePath)!;
        for (var slot = 1; slot <= 5; slot++)
        {
            var rom = IniFile.ReadValue(config, slot == 5 ? "GBPlayerRom" : "Rom" + slot, "GBA")?.Trim('"');
            if (string.IsNullOrWhiteSpace(rom))
            {
                continue;
            }

            rom = Path.GetFullPath(Path.Combine(program, rom));
            RequireRomMedia(rom);
            yield return Path.ChangeExtension(rom, null) + "-" + slot + ".sav";
        }
    }

    private static void RequireRomMedia(string rom)
    {
        if (!File.Exists(rom) && !Directory.Exists(rom))
        {
            throw new InvalidDataException("Reconnect the ROM media for " + rom
                                                                          + " before converting content-directory data to portable storage. The active version is preserved.");
        }
    }

    private static void PreserveDolphinGbaSaves(EmulatorInstallation installed, IReadOnlyList<string> saves,
        CancellationToken token)
    {
        var config = Path.Combine(installed.DataPath, "Config", "Dolphin.ini");
        if (!EmulatorPortable.IsTrue(IniFile.ReadValue(config, "SavesInRomPath", "GBA")))
        {
            return;
        }

        var setting = IniFile.ReadValue(config, "SavesPath", "GBA")?.Trim('"');
        var destination = string.IsNullOrWhiteSpace(setting)
            ? Path.Combine(installed.DataPath, "GBA", "Saves")
            : EmulatorPortable.ResolveSettingPath(installed, "GBA", "SavesPath", setting);
        if (!EmulatorPortable.IsUnder(installed.DataPath, destination))
        {
            throw new InvalidDataException(
                "Dolphin GBA saves must be preserved inside owned portable data before rebinding.");
        }

        foreach (var save in saves)
        {
            CopyPath(save, Path.Combine(destination, Path.GetFileName(save)), token);
        }

        IniFile.SetValue(config, "SavesPath", destination.Replace('\\', '/'), "GBA");
        IniFile.SetValue(config, "SavesInRomPath", "False", "GBA");
    }

    private static void CopyPath(string source, string destination, CancellationToken token, bool required = false)
    {
        if (required)
        {
            RequireDataSource(source);
        }

        if (Same(source, destination))
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

    private static void Link(string program, string name, string data, CancellationToken token, bool staged)
    {
        var link = Path.Combine(program, name);
        var directory = new DirectoryInfo(link);
        string? backup = null;
        if (directory.LinkTarget is not null)
        {
            if (directory.Exists && Same(directory.ResolveLinkTarget(true)!.FullName, data))
            {
                return;
            }

            CopyData(EmulatorPortable.PhysicalPath(link), data, token, overwriteExisting: !staged);
            Directory.Delete(link); // Only the old alias, never its target.
        }
        else if (directory.Exists)
        {
            CopyData(link, data, token, overwriteExisting: !staged);
            backup = link + ".before-portable-" + Guid.NewGuid().ToString("N");
            Directory.Move(link, backup);
        }

        try
        {
            CreateAlias(link, data);
        }
        catch
        {
            if (backup is not null && Directory.Exists(backup) && !Directory.Exists(link))
            {
                Directory.Move(backup, link);
            }

            throw;
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
        if (!StoragePaths.IsUnder(root, full) || Same(full, root))
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
        var name = installed.DefinitionId switch
        {
            "eden" => "user", "rpcs3" => "portable", "dolphin" => "User", _ => ""
        };
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
                                         && Same(EmulatorPortable.PhysicalPath(directory),
                                             EmulatorPortable.PhysicalPath(retainedData)))
                {
                    aliases.Add(Path.GetFullPath(directory));
                }
            }
        }

        CopyData(version, destination, token, excludedLinks: aliases);
    }

    private static bool Same(string left, string right)
    {
        return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }
}
