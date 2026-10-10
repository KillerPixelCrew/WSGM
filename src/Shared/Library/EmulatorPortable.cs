using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WSGM.Core;

/// <summary>Upstream Windows portable layouts, shared by registration and fresh launch admission.</summary>
internal static class EmulatorPortable
{
    internal static readonly string[] RetroArchFlags =
    [
        "savefiles_in_content_dir", "savestates_in_content_dir", "systemfiles_in_content_dir",
        "screenshots_in_content_dir"
    ];

    internal static readonly string[] RetroArchDataPaths =
    [
        "system_directory", "savefile_directory", "savestate_directory", "input_remapping_directory",
        "playlist_directory", "screenshot_directory", "cache_directory", "rgui_config_directory",
        "core_options_path", "cheat_database_path", "recording_output_directory", "recording_config_directory",
        "log_dir", "content_history_path", "content_favorites_path", "content_image_history_path",
        "content_music_history_path", "content_video_history_path"
    ];

    internal static string ReadExternalData(string id, string program)
    {
        if (id == "pcsx2" && File.Exists(Path.Combine(program, "portable.txt")) &&
            Path.IsPathRooted(File.ReadAllText(Path.Combine(program, "portable.txt")).Trim()))
        {
            throw new InvalidDataException("PCSX2 portable.txt requires a relative data path. " + Instruction(id));
        }

        var data = id switch
        {
            "retroarch" when File.Exists(Path.Combine(program, "retroarch.cfg")) => program,
            "duckstation" when File.Exists(Path.Combine(program, "portable.txt")) ||
                               File.Exists(Path.Combine(program, "settings.ini")) => program,
            "pcsx2" when File.Exists(Path.Combine(program, "portable.txt")) => Path.GetFullPath(Path.Combine(program,
                File.ReadAllText(Path.Combine(program, "portable.txt")).Trim())),
            "pcsx2" when File.Exists(Path.Combine(program, "portable.ini")) => program,
            "eden" when Directory.Exists(Path.Combine(program, "user")) => Path.Combine(program, "user"),
            "rpcs3" when Directory.Exists(Path.Combine(program, "portable")) => Path.Combine(program, "portable"),
            "dolphin" when File.Exists(Path.Combine(program, "portable.txt")) => Path.Combine(program, "User"),
            _ => throw new InvalidDataException(Instruction(id))
        };
        data = PhysicalPath(data);
        if (!Directory.Exists(data) || !IsUnder(program, data))
        {
            throw new InvalidDataException(
                "This external emulator's portable data is outside its installation folder. " + Instruction(id));
        }

        return data;
    }

    internal static void Verify(EmulatorInstallation installed)
    {
        var program = Path.GetDirectoryName(Path.GetFullPath(installed.ExecutablePath))!;
        if (installed.Managed && (!StoragePaths.IsUnder(installed.Root, program) || !installed.OwnsData))
        {
            throw new InvalidDataException("The managed emulator has no owned portable installation. Use Repair.");
        }

        if (installed.Managed)
        {
            var owner = Directory.GetParent(installed.Root)?.Parent?.FullName;
            if (owner is null || (!IsUnder(installed.Root, installed.DataPath) &&
                                  !IsUnder(Path.Combine(owner, "EmulatorData", installed.Id),
                                      installed.DataPath)))
            {
                throw new InvalidDataException(
                    "The emulator data is outside its owned portable roots. Use Repair to preserve and bind it.");
            }
        }

        if (!installed.Managed)
        {
            var actual = ReadExternalData(installed.DefinitionId, program);
            if (!Same(actual, installed.DataPath))
            {
                throw new InvalidDataException("The external portable data location changed. Register it again.");
            }
        }
        else
        {
            switch (installed.DefinitionId)
            {
                case "retroarch":
                    if (!File.Exists(Path.Combine(installed.DataPath, "retroarch.cfg")))
                    {
                        throw new InvalidDataException("The portable RetroArch configuration is missing. Use Repair.");
                    }

                    break;
                case "pcsx2":
                    var marker = Path.Combine(program, "portable.txt");
                    if (!File.Exists(marker) || Path.IsPathRooted(File.ReadAllText(marker).Trim()) ||
                        !Same(Path.Combine(program, File.ReadAllText(marker).Trim()), installed.DataPath))
                    {
                        throw new InvalidDataException("The portable PCSX2 data binding changed. Use Repair.");
                    }

                    break;
                case "duckstation":
                    if (!(File.Exists(Path.Combine(program, "portable.txt")) ||
                          File.Exists(Path.Combine(program, "settings.ini"))) ||
                        !Same(program, installed.DataPath))
                    {
                        throw new InvalidDataException("The portable DuckStation data binding changed. Use Repair.");
                    }

                    break;
                case "eden": VerifyDirectory(program, "user", installed.DataPath); break;
                case "rpcs3": VerifyDirectory(program, "portable", installed.DataPath); break;
                case "dolphin":
                    if (!File.Exists(Path.Combine(program, "portable.txt")))
                    {
                        throw new InvalidDataException("Dolphin's portable marker is missing. Use Repair.");
                    }

                    VerifyDirectory(program, "User", installed.DataPath);
                    break;
                default: throw new InvalidDataException("This emulator has no reviewed portable policy.");
            }
        }

        if (installed.DefinitionId == "retroarch")
        {
            VerifyRetroArchConfig(Path.Combine(installed.DataPath, "retroarch.cfg"), installed.DataPath, true);
            var overrides = IniFile
                .ReadValue(Path.Combine(installed.DataPath, "retroarch.cfg"), "rgui_config_directory")?.Trim('"');
            if (!string.IsNullOrWhiteSpace(overrides))
            {
                var directory = Path.GetFullPath(Path.Combine(installed.DataPath, overrides));
                if (Directory.Exists(directory))
                {
                    foreach (var config in ConfigFiles(directory))
                    {
                        VerifyRetroArchConfig(config, installed.DataPath, false);
                    }
                }
            }
        }

        foreach (var (file, section, key, folder) in DataFolders(installed.DefinitionId))
        {
            var config = Path.Combine(installed.DataPath, file);
            if (File.Exists(config) && !IsUnder(installed.DataPath, config))
            {
                throw new InvalidDataException("The portable configuration is linked outside its data folder. " +
                                               (installed.Managed
                                                   ? "Use Repair to preserve and bind it."
                                                   : "Copy the configuration into the portable folder before registering."));
            }

            var value = IniFile.ReadValue(config, key, section)?.Trim('"');
            var path = string.IsNullOrWhiteSpace(value)
                ? Path.Combine(installed.DataPath, folder)
                : ResolveSettingPath(installed, section, key, value);
            if (!IsUnder(installed.DataPath, path))
            {
                throw new InvalidDataException($"The portable {key} setting points outside the emulator data folder. " +
                                               (installed.Managed
                                                   ? "Use Repair to preserve and bind that data."
                                                   : "Copy that data into the portable folder and change the setting before registering."));
            }
        }

        if (installed.DefinitionId == "rpcs3")
        {
            var config = Path.Combine(installed.DataPath, "vfs.yml");
            if (File.Exists(config) && !IsUnder(installed.DataPath, config))
            {
                throw new InvalidDataException(
                    "RPCS3 vfs.yml is linked outside its portable data folder. Use Repair or copy it into portable/ before registering.");
            }

            foreach (var (_, path, _) in RpcPaths(installed.DataPath))
            {
                if (!IsUnder(installed.DataPath, path))
                {
                    throw new InvalidDataException(
                        "RPCS3 vfs.yml redirects persistent data outside its portable folder. " +
                        (installed.Managed
                            ? "Use Repair to preserve and bind that data."
                            : "Copy that data into portable/ and reset its VFS path before registering."));
                }
            }
        }

        if (installed.DefinitionId == "dolphin" && IsTrue(IniFile.ReadValue(
                Path.Combine(installed.DataPath, "Config", "Dolphin.ini"), "SavesInRomPath", "GBA")))
        {
            throw new InvalidDataException("Dolphin GBA saves are redirected beside ROMs. " +
                                           (installed.Managed
                                               ? "Use Repair to preserve and bind them."
                                               : "Disable SavesInRomPath before registering this portable installation."));
        }
    }

    internal static IEnumerable<(string File, string Section, string Key, string Folder)> DataFolders(string id)
    {
        if (id == "duckstation")
        {
            foreach (var (key, folder) in new[]
                     {
                         ("Cache", "cache"), ("Cheats", "cheats"), ("Covers", "covers"), ("GameIcons", "gameicons"),
                         ("GameSettings", "gamesettings"), ("InputProfiles", "inputprofiles"), ("Patches", "patches"),
                         ("SaveStates", "savestates"), ("Screenshots", "screenshots"), ("Shaders", "shaders"),
                         ("Subchannels", "subchannels"), ("Textures", "textures"), ("UserResources", "resources"),
                         ("Videos", "videos")
                     })
            {
                yield return ("settings.ini", "Folders", key, folder);
            }

            yield return ("settings.ini", "MemoryCards", "Directory", "memcards");
            for (var slot = 1; slot <= 8; slot++)
            {
                yield return ("settings.ini", "MemoryCards", "Card" + slot + "Path", "memcards");
            }
        }
        else if (id == "pcsx2")
        {
            foreach (var (key, folder) in new[]
                     {
                         ("Snapshots", "snaps"), ("Savestates", "sstates"), ("MemoryCards", "memcards"),
                         ("Logs", "logs"), ("Cheats", "cheats"), ("Patches", "patches"), ("UserResources", "resources"),
                         ("Cache", "cache"), ("Textures", "textures"), ("InputProfiles", "inputprofiles"),
                         ("Videos", "videos"),
                         ("DebuggerLayouts", "debuggerlayouts"), ("DebuggerSettings", "debuggersettings")
                     })
            {
                yield return ("inis/PCSX2.ini", "Folders", key, folder);
            }
        }
        else if (id == "eden")
        {
            yield return ("config/qt-config.ini", "DataStorage", "nand_directory", "nand");
            yield return ("config/qt-config.ini", "DataStorage", "sdmc_directory", "sdmc");
            yield return ("config/qt-config.ini", "DataStorage", "load_directory", "load");
            yield return ("config/qt-config.ini", "DataStorage", "dump_directory", "dump");
            yield return ("config/qt-config.ini", "DataStorage", "tas_directory", "tas");
            yield return ("config/qt-config.ini", "DataStorage", "save_directory", "nand");
        }
        else if (id == "dolphin")
        {
            yield return ("Config/Dolphin.ini", "General", "NANDRootPath", "Wii");
            yield return ("Config/Dolphin.ini", "General", "DumpPath", "Dump");
            yield return ("Config/Dolphin.ini", "General", "LoadPath", "Load");
            yield return ("Config/Dolphin.ini", "General", "ResourcePackPath", "ResourcePacks");
            yield return ("Config/Dolphin.ini", "General", "WiiSDCardPath", "Wii");
            yield return ("Config/Dolphin.ini", "General", "WiiSDCardSyncFolder", "Wii/sdcard");
            yield return ("Config/Dolphin.ini", "General", "WFSPath", "WFS");
            yield return ("Config/Dolphin.ini", "GBA", "SavesPath", "GBA/Saves");
            yield return ("Config/Dolphin.ini", "Core", "GCIFolderAPath", "GC/USA/Card A");
            yield return ("Config/Dolphin.ini", "Core", "GCIFolderBPath", "GC/USA/Card B");
            yield return ("Config/Dolphin.ini", "Core", "GCIFolderAPathOverride", "GC/USA/Card A");
            yield return ("Config/Dolphin.ini", "Core", "GCIFolderBPathOverride", "GC/USA/Card B");
            yield return ("Config/Dolphin.ini", "Core", "MemcardAPath", "GC");
            yield return ("Config/Dolphin.ini", "Core", "MemcardBPath", "GC");
        }
    }

    internal static string ResolveSettingPath(EmulatorInstallation installed, string section, string key, string value)
    {
        var root = installed.DataPath;
        if (installed.DefinitionId is "eden" or "dolphin")
        {
            root = Path.GetDirectoryName(installed.ExecutablePath)!;
        }

        if (installed.DefinitionId == "duckstation" && section == "MemoryCards" && key != "Directory")
        {
            var cards = IniFile.ReadValue(Path.Combine(installed.DataPath, "settings.ini"), "Directory", "MemoryCards")
                ?.Trim('"');
            root = Path.GetFullPath(Path.Combine(installed.DataPath,
                string.IsNullOrWhiteSpace(cards) ? "memcards" : cards));
        }

        return Path.GetFullPath(Path.Combine(root, value));
    }

    internal static IEnumerable<(string Key, string Path, string Folder)> RpcPaths(string data)
    {
        var file = Path.Combine(data, "vfs.yml");
        if (!File.Exists(file))
        {
            yield break;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(file))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            var key = line[..colon].Trim().Trim('"', '\'');
            var value = line[(colon + 1)..].Trim();
            if (value.StartsWith('"'))
            {
                value = JsonSerializer.Deserialize<string>(value) ?? "";
            }
            else if (value.StartsWith('\''))
            {
                value = value.Trim('\'').Replace("''", "'", StringComparison.Ordinal);
            }

            values[key] = value;
        }

        var root = values.GetValueOrDefault("$(EmulatorDir)", "");
        root = string.IsNullOrWhiteSpace(root)
            ? data
            : Path.GetFullPath(root.Replace("$(EmulatorDir)",
                data + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        if (!Same(root, data))
        {
            yield return ("$(EmulatorDir)", root, "");
        }

        foreach (var folder in new[] { "dev_hdd0", "dev_hdd1", "dev_flash", "dev_flash2", "dev_flash3" })
        {
            var key = "/" + folder + "/";
            var value = values.GetValueOrDefault(key, "$(EmulatorDir)" + folder + "/");
            if (string.IsNullOrWhiteSpace(value))
            {
                value = "$(EmulatorDir)" + folder + "/";
            }

            yield return (key, Path.GetFullPath(value.Replace("$(EmulatorDir)",
                root + Path.DirectorySeparatorChar, StringComparison.Ordinal)), folder);
        }
    }

    internal static void VerifyArguments(EmulatorInstallation installed, IReadOnlyList<string> arguments)
    {
        var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(installed.ExecutablePath))!;

        string Resolve(string value)
        {
            return Path.GetFullPath(Path.Combine(workingDirectory, value));
        }

        var option = installed.DefinitionId == "retroarch" ? "-c" : installed.DefinitionId == "dolphin" ? "-u" : "";
        var expected = installed.DefinitionId == "retroarch"
            ? Path.Combine(installed.DataPath, "retroarch.cfg")
            : installed.DataPath;
        var found = option.Length == 0;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "--")
            {
                break;
            }

            if (installed.DefinitionId == "pcsx2" && argument == "-datapath")
            {
                throw new InvalidDataException("The legacy PCSX2 data override bypasses portable mode. Use Repair.");
            }

            var longOption = installed.DefinitionId == "retroarch" ? "--config" : "--user";
            if (option.Length > 0 && (argument == option || argument == longOption ||
                                      argument.StartsWith(longOption + "=", StringComparison.Ordinal) ||
                                      (argument.StartsWith(option, StringComparison.Ordinal) &&
                                       argument.Length > option.Length)))
            {
                var value = argument.StartsWith(longOption + "=", StringComparison.Ordinal)
                    ? argument[(longOption.Length + 1)..]
                    : argument.Length > option.Length && argument.StartsWith(option, StringComparison.Ordinal)
                        ? argument[option.Length..]
                        : ++index < arguments.Count
                            ? arguments[index]
                            : "";
                if (value.Length == 0 || !Same(PhysicalPath(Resolve(value)), PhysicalPath(expected)))
                {
                    throw new InvalidDataException(
                        "The emulator launch overrides its portable data binding. Use its managed launch arguments.");
                }

                found = true;
            }

            if (installed.DefinitionId == "retroarch" && (argument is "-s" or "--save" or "-S" or "--savestate" ||
                                                          argument.StartsWith("--save=", StringComparison.Ordinal) ||
                                                          argument.StartsWith("--savestate=",
                                                              StringComparison.Ordinal) ||
                                                          (argument.StartsWith("-s", StringComparison.Ordinal) &&
                                                           argument.Length > 2) ||
                                                          (argument.StartsWith("-S", StringComparison.Ordinal) &&
                                                           argument.Length > 2)))
            {
                var equals = argument.IndexOf('=');
                var value = equals >= 0
                    ? argument[(equals + 1)..]
                    : argument.Length > 2 && argument[1] != '-'
                        ? argument[2..]
                        : ++index < arguments.Count
                            ? arguments[index]
                            : "";
                if (value.Length == 0 || !IsUnder(installed.DataPath, Resolve(value)))
                {
                    throw new InvalidDataException("The RetroArch save argument is outside its portable data folder.");
                }
            }

            if (installed.DefinitionId == "retroarch" && (argument == "--appendconfig" ||
                                                          argument.StartsWith("--appendconfig=",
                                                              StringComparison.Ordinal)))
            {
                var value = argument.StartsWith("--appendconfig=", StringComparison.Ordinal) ? argument[15..] :
                    ++index < arguments.Count ? arguments[index] : "";
                if (value.Length == 0)
                {
                    throw new InvalidDataException("The RetroArch appended configuration is missing.");
                }

                foreach (var config in value.Split('|'))
                {
                    var path = Resolve(config);
                    if (!IsUnder(installed.DataPath, path))
                    {
                        throw new InvalidDataException("A RetroArch override is outside its portable data folder.");
                    }

                    VerifyRetroArchConfig(path, installed.DataPath, false);
                }
            }

            if (installed.DefinitionId == "dolphin" && (argument is "-C" or "--config"
                                                        || argument.StartsWith("--config=", StringComparison.Ordinal)
                                                        || (argument.StartsWith("-C", StringComparison.Ordinal) &&
                                                            argument.Length > 2)))
            {
                var value = argument.StartsWith("--config=", StringComparison.Ordinal) ? argument[9..]
                    : argument.Length > 2 && argument[1] != '-' ? argument[2..]
                    : ++index < arguments.Count ? arguments[index] : "";
                var equals = value.IndexOf('=');
                if (equals < 0)
                {
                    continue;
                }

                var key = value[..equals].Split('.');
                if (key.Length != 3 || key[0] != "Dolphin")
                {
                    continue;
                }

                var setting = value[(equals + 1)..];
                if (key[1] == "GBA" && key[2] == "SavesInRomPath" && IsTrue(setting))
                {
                    throw new InvalidDataException(
                        "The Dolphin launch redirects GBA saves beside ROMs. Disable that override to keep saves portable.");
                }

                if (setting.Length > 0 && DataFolders("dolphin")
                                           .Any(folder => folder.Section == key[1] && folder.Key == key[2])
                                       && !IsUnder(installed.DataPath, Resolve(setting)))
                {
                    throw new InvalidDataException("The Dolphin launch redirects " + key[2] +
                                                   " outside its portable data folder.");
                }
            }
        }

        if (!found)
        {
            throw new InvalidDataException(
                "The emulator launch is missing its portable data argument. Use the default emulator arguments.");
        }
    }

    internal static string[] BindArguments(EmulatorInstallation installed, string[] arguments)
    {
        var shortOption = installed.DefinitionId == "retroarch" ? "-c" :
            installed.DefinitionId == "dolphin" ? "-u" : "";
        if (shortOption.Length > 0)
        {
            var longOption = installed.DefinitionId == "retroarch" ? "--config" : "--user";
            var options = arguments.TakeWhile(argument => argument != "--");
            if (!options.Any(argument => argument == longOption ||
                                         argument.StartsWith(longOption + "=", StringComparison.Ordinal) ||
                                         argument.StartsWith(shortOption, StringComparison.Ordinal)))
            {
                arguments =
                [
                    shortOption,
                    installed.DefinitionId == "retroarch"
                        ? Path.Combine(installed.DataPath, "retroarch.cfg")
                        : installed.DataPath,
                    .. arguments
                ];
            }
        }

        VerifyArguments(installed, arguments);
        return arguments;
    }

    private static void VerifyRetroArchConfig(string config, string data, bool required)
    {
        if (!IsUnder(data, config))
        {
            throw new InvalidDataException("The RetroArch configuration redirects outside its portable data folder.");
        }

        if (!File.Exists(config))
        {
            throw new InvalidDataException("The portable RetroArch configuration is missing.");
        }

        foreach (var key in RetroArchDataPaths)
        {
            var value = IniFile.ReadValue(config, key)?.Trim('"');
            if (string.IsNullOrWhiteSpace(value) && !required)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(value) || value == "default" ||
                !IsUnder(data, Path.GetFullPath(Path.Combine(data, value))))
            {
                throw new InvalidDataException(
                    $"RetroArch {key} must name a path inside its portable data folder. Configure it before registering, or use Repair for a managed install.");
            }
        }

        foreach (var key in RetroArchFlags)
        {
            if (IsTrue(IniFile.ReadValue(config, key)))
            {
                throw new InvalidDataException(
                    $"RetroArch {key} writes beside the ROM. Disable it to keep data portable.");
            }
        }
    }

    private static void VerifyDirectory(string program, string name, string expected)
    {
        var directory = new DirectoryInfo(Path.Combine(program, name));
        if (!directory.Exists || !Same(directory.ResolveLinkTarget(true)?.FullName ?? directory.FullName, expected))
        {
            throw new InvalidDataException("The emulator's portable " + name + " directory changed. Use Repair.");
        }
    }

    internal static bool IsTrue(string? value)
    {
        return value?.Trim().Trim('"') is { } text
               && (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1");
    }

    internal static bool IsUnder(string root, string path)
    {
        return StoragePaths.IsUnder(PhysicalPath(root), PhysicalPath(path));
    }

    /// <summary>Resolves existing parent links too, while allowing future files inside a real owned directory.</summary>
    internal static string PhysicalPath(string path)
    {
        return Resolve(path, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static string Resolve(string path, HashSet<string> links)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            if (!links.Add(current))
            {
                throw new InvalidDataException("The portable data path contains a recursive link: " + current);
            }

            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            var target = info.ResolveLinkTarget(true);
            if (target is null || !target.Exists)
            {
                throw new InvalidDataException("The portable data path contains a dangling link: " + current);
            }

            var link = current;
            try
            {
                current = Resolve(target.FullName, links);
            }
            finally
            {
                links.Remove(link);
            }
        }

        return current;
    }

    internal static IEnumerable<string> ConfigFiles(string root, string pattern = "*.cfg")
    {
        var pending = new Stack<(string Path, HashSet<string> Ancestors)>();
        pending.Push((root, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        while (pending.Count > 0)
        {
            var (path, ancestors) = pending.Pop();
            var actual = PhysicalPath(path);
            if (!ancestors.Add(actual))
            {
                throw new InvalidDataException(
                    "The portable configuration contains a recursive directory link: " + path);
            }

            foreach (var file in Directory.EnumerateFiles(path, pattern))
            {
                yield return file;
            }

            foreach (var directory in Directory.EnumerateDirectories(path))
            {
                pending.Push((directory, new HashSet<string>(ancestors, StringComparer.OrdinalIgnoreCase)));
            }
        }
    }

    private static bool Same(string left, string right)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(left))
            .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    }

    private static string Instruction(string id)
    {
        return id switch
        {
            "retroarch" => "Use a portable RetroArch folder with retroarch.cfg beside retroarch.exe.",
            "duckstation" =>
                "Create portable.txt beside DuckStation and copy your existing user files there before registering it.",
            "pcsx2" =>
                "Create portable.txt beside PCSX2, optionally containing a relative data-folder path, and copy your user files into that folder.",
            "eden" =>
                "Create a portable user folder beside eden.exe and copy your existing Eden data into it before registering it.",
            "rpcs3" =>
                "Create a portable folder beside rpcs3.exe and copy your existing RPCS3 data into it before registering it.",
            "dolphin" =>
                "Create portable.txt beside Dolphin.exe and copy your existing Dolphin data into its User folder before registering it.",
            _ => "Choose a reviewed portable emulator installation."
        };
    }
}
