using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace WSGM.Core;

/// <summary>
///     RetroArch's portable conversion: reads the ROM paths its content-directory data depends on and rewrites
///     every main, override and per-game config to the retained portable data.
/// </summary>
internal static class RetroArchPortableData
{
    private static readonly string[] RetroArchSorting =
    [
        "sort_savefiles_enable", "sort_savestates_enable", "sort_savefiles_by_content_enable",
        "sort_savestates_by_content_enable"
    ];

    /// <summary>Reads the ROM paths whose content-directory saves and states move with the data.</summary>
    /// <param name="previous">Installation whose current RetroArch configuration is read.</param>
    /// <param name="knownRomPaths">Optional ROM paths known to the library.</param>
    /// <param name="token">Cancels the read.</param>
    /// <returns>The ROM paths to preserve sidecars for.</returns>
    internal static IReadOnlyCollection<string> ReadRomPaths(EmulatorInstallation previous,
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
                else if (EmulatorPortable.Same(config, main))
                {
                    playlists.Add(Path.Combine(previous.DataPath, key[..^5] + ".lpl"));
                }
            }

            var setting = IniFile.ReadValue(config, "playlist_directory")?.Trim('"');
            var directory = string.IsNullOrWhiteSpace(setting) || setting == "default"
                ? EmulatorPortable.Same(config, main) ? Path.Combine(previous.DataPath, "playlists") : null
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
            EmulatorPortableSetup.RequireRomMedia(archive < 0 ? rom : rom[..archive]);
        }

        var mainFlags = RetroArchFlags(main, null);
        foreach (var config in configs)
        {
            foreach (var rom in paths)
            {
                var flags = EmulatorPortable.Same(config, main) ? mainFlags : RetroArchFlags(config, mainFlags, rom);
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

    /// <summary>Rewrites RetroArch's configs to the retained portable data and preserves content-directory files.</summary>
    /// <param name="installed">Installation whose data path is already the portable data.</param>
    /// <param name="previous">Previous installation, or null on a first conversion.</param>
    /// <param name="token">Cancels the copy.</param>
    /// <param name="knownRomPaths">ROM paths read before the data moved.</param>
    internal static void PrepareConfigs(EmulatorInstallation installed, EmulatorInstallation? previous,
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
            var mainConfig = EmulatorPortable.Same(config, main);
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
                    EmulatorPortableSetup.CopyPath(source, destination, token,
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
            if (!EmulatorPortable.Same(core, config) && File.Exists(core))
            {
                inherited = RetroArchFlags(core, inherited, originalValues: originalValues);
            }

            if (rom is not null)
            {
                var (parent, stem) = RetroArchContentName(rom);
                var content = Path.Combine(directory, Path.GetFileName(parent) + ".cfg");
                if (Path.GetFileNameWithoutExtension(config).Equals(stem, StringComparison.OrdinalIgnoreCase)
                    && !EmulatorPortable.Same(content, config) && !EmulatorPortable.Same(content, core) && File.Exists(content))
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
            EmulatorPortableSetup.RequireRomMedia(archive < 0 ? rom : rom[..archive]);
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

                    EmulatorPortableSetup.CopyPath(source, Path.Combine(systemDirectory, relative), token);
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
                        EmulatorPortableSetup.CopyPath(file, target, token);
                        if (key == "savestate_directory" && File.Exists(file + ".png"))
                        {
                            EmulatorPortableSetup.CopyPath(file + ".png", target + ".png", token);
                        }
                    }
                }
            }
        }
    }
}
