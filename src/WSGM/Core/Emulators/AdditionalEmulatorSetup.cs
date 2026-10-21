using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace WSGM.Core;

/// <summary>Initializes upstream portable markers without replacing user configuration.</summary>
internal static class AdditionalEmulatorSetup
{
    internal static void Configure(EmulatorInstallation installed)
    {
        var program = Path.GetDirectoryName(installed.ExecutablePath)!;
        foreach (var marker in EmulatorPortableProfiles.Markers(installed.DefinitionId))
        {
            var path = Path.Combine(program, marker);
            if (Path.HasExtension(marker))
            {
                if (!File.Exists(path))
                {
                    AtomicFile.WriteText(path, "", durable: true);
                }
            }
            else
            {
                Directory.CreateDirectory(path);
            }
        }

        switch (installed.DefinitionId)
        {
            case "ppsspp":
                // The Windows ZIP is portable unless this installer-created marker opts out.
                if (File.Exists(Path.Combine(program, "installed.txt")))
                {
                    throw new InvalidDataException("This PPSSPP package enables installed mode instead of portable mode.");
                }

                break;
            case "mgba":
                IniFile.SetValue(Path.Combine(program, "portable.ini"), "path", ".", "portable");
                SetDefault(installed, "config.ini", "savegamePath", "saves", "ports.qt");
                SetDefault(installed, "config.ini", "savestatePath", "states", "ports.qt");
                SetDefault(installed, "config.ini", "screenshotPath", "screenshots", "ports.qt");
                break;
            case "flycast":
                // Existing emu.cfg next to the binary is Flycast's portable-mode signal.
                Directory.CreateDirectory(Path.Combine(program, "data"));
                break;
            case "scummvm":
                SetDefault(installed, "scummvm.ini", "savepath", "saves", "scummvm");
                break;
            case "melonds":
                SetDefault(installed, "melonDS.toml", "SaveFilePath", "saves", "Instance0", quoted: true);
                SetDefault(installed, "melonDS.toml", "SavestatePath", "states", "Instance0", quoted: true);
                break;
            case "ares":
                SetBmlPaths(installed);
                break;
            case "xemu":
                SetTomlDefault(installed, "bootrom_path", "bios/mcpx_1.0.bin");
                SetTomlDefault(installed, "flashrom_path", "bios/bios.bin");
                SetTomlDefault(installed, "hdd_path", "bios/xbox_hdd.qcow2");
                SetTomlDefault(installed, "eeprom_path", "eeprom.bin");
                break;
        }
    }

    internal static void RebaseOwnedPaths(EmulatorInstallation installed, EmulatorInstallation? previous,
        CancellationToken token)
    {
        if (previous is null || previous.DataPath == installed.DataPath)
        {
            return;
        }

        // CopyData already preserves user bytes/collisions. Rebind only references to the retired
        // owned data root; intentional paths elsewhere keep their original external semantics.
        foreach (var file in Directory.EnumerateFiles(installed.DataPath, "*", new EnumerationOptions
                 { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            token.ThrowIfCancellationRequested();
            if (Path.GetRelativePath(installed.DataPath, file).Split(Path.DirectorySeparatorChar)
                    .Any(part => part.StartsWith(".portable-snapshot", StringComparison.Ordinal)) ||
                Path.GetExtension(file).ToLowerInvariant() is not
                    (".ini" or ".cfg" or ".toml" or ".xml" or ".json" or ".bml" or ".yml") ||
                new FileInfo(file).Length > 4 * 1024 * 1024)
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var changed = text;
            foreach (var separator in new[] { "\\\\", "\\", "/" })
            {
                var before = previous.DataPath.Replace("\\", separator, StringComparison.Ordinal)
                    .Replace("/", separator, StringComparison.Ordinal).TrimEnd('\\', '/');
                var after = installed.DataPath.Replace("\\", separator, StringComparison.Ordinal)
                    .Replace("/", separator, StringComparison.Ordinal).TrimEnd('\\', '/');
                changed = Regex.Replace(changed, Regex.Escape(before) + "(?=[\\\\/\"'<>\\r\\n]|$)",
                    _ => after, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }

            if (changed != text)
            {
                File.Copy(file, file + ".before-portable-" + Guid.NewGuid().ToString("N"));
                AtomicFile.WriteText(file, changed, durable: true);
            }
        }
    }

    private static void SetBmlPaths(EmulatorInstallation installed)
    {
        var file = Path.Combine(installed.DataPath, "settings.bml");
        var lines = File.ReadAllLines(file).ToList();
        var index = lines.FindIndex(line => line == "Paths");
        if (index < 0)
        {
            lines.Add("Paths");
            index = lines.Count - 1;
        }

        foreach (var (key, folder) in new[]
                 {
                     ("Saves", "Saves"), ("Screenshots", "Screenshots"), ("Firmware", "Firmware"),
                     ("Debugging", "Debugging")
                 })
        {
            var end = index + 1;
            while (end < lines.Count && (lines[end].Length == 0 || char.IsWhiteSpace(lines[end][0])))
            {
                end++;
            }

            var child = lines.FindIndex(index + 1, end - index - 1,
                line => line.StartsWith("  " + key + ":", StringComparison.Ordinal));
            if (child >= 0 && lines[child][(lines[child].IndexOf(':') + 1)..].Trim().Length > 0)
            {
                continue;
            }

            var destination = Path.Combine(installed.DataPath, folder).Replace('\\', '/') + "/";
            Directory.CreateDirectory(destination);
            var value = "  " + key + ": " + destination;
            if (child >= 0)
            {
                lines[child] = value;
            }
            else
            {
                lines.Insert(end, value);
            }
        }

        AtomicFile.WriteText(file, string.Join(Environment.NewLine, lines) + Environment.NewLine, durable: true);
    }

    private static void SetTomlDefault(EmulatorInstallation installed, string key, string relative)
    {
        var file = Path.Combine(installed.DataPath, "xemu.toml");
        if (string.IsNullOrWhiteSpace(IniFile.ReadValue(file, key, "sys.files")))
        {
            IniFile.SetValue(file, key,
                "\"" + Path.Combine(installed.DataPath, relative).Replace('\\', '/') + "\"", "sys.files");
        }
    }

    private static void SetDefault(EmulatorInstallation installed, string file, string key, string folder,
        string section, bool quoted = false)
    {
        var path = Path.Combine(installed.DataPath, file);
        if (!string.IsNullOrWhiteSpace(IniFile.ReadValue(path, key, section)?.Trim('"', '\'')))
        {
            return;
        }

        var destination = Path.Combine(installed.DataPath, folder);
        Directory.CreateDirectory(destination);
        var value = destination.Replace('\\', '/');
        IniFile.SetValue(path, key, quoted ? "\"" + value + "\"" : value, section);
    }
}
