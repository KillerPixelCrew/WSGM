using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using SteamUiToolkit;

namespace WSGM.Core;

/// <summary>Reads Steam's selected app launch metadata without guessing a working directory from DLL placement.</summary>
internal static class GameToolLaunchDirectory
{
    internal const string Unavailable = "DXVK reads dxvk.conf from the game's working directory, which WSGM could not determine for this game.";

    internal static string Resolve(uint appId, bool shortcut, SteamAppDetails details, string root)
    {
        if (shortcut)
        {
            return Contained(root, details.ShortcutStartDir.Trim().Trim('"'));
        }

        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var steam = key?.GetValue("SteamPath") as string ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Read(Path.Combine(steam, "appcache", "appinfo.vdf"), appId, root);
    }

    /// <summary>Reads binary appinfo formats 27-29, including the version-29 shared key string table.</summary>
    internal static string Read(string path, uint appId, string root)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
            var magic = reader.ReadUInt32();
            if (magic is not (0x07564427 or 0x07564428 or 0x07564429))
            {
                return "";
            }
            reader.ReadUInt32();
            string[]? table = null;
            var start = file.Position;
            if (magic == 0x07564429)
            {
                var offset = reader.ReadInt64();
                start = file.Position;
                if (offset < start || offset >= file.Length)
                {
                    return "";
                }
                file.Position = offset;
                var count = reader.ReadInt32();
                if (count is < 0 or > 1000000)
                {
                    return "";
                }
                table = new string[count];
                for (var index = 0; index < count; index++)
                {
                    table[index] = String(reader);
                }
                file.Position = start;
            }

            while (file.Position + 8 <= file.Length)
            {
                var id = reader.ReadUInt32();
                if (id == 0)
                {
                    return "";
                }
                var size = reader.ReadUInt32();
                var end = checked(file.Position + size);
                if (size is < 40 or > 32 * 1024 * 1024 || end > file.Length)
                {
                    return "";
                }
                if (id != appId)
                {
                    file.Position = end;
                    continue;
                }
                file.Position += magic == 0x07564427 ? 40 : 60;
                var launches = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                var stack = new List<string>();
                while (file.Position < end)
                {
                    var type = reader.ReadByte();
                    if (type is 8 or 11)
                    {
                        if (stack.Count == 0)
                        {
                            break;
                        }
                        stack.RemoveAt(stack.Count - 1);
                        continue;
                    }

                    var name = table is null ? String(reader) : table[reader.ReadUInt32()];
                    if (type == 0)
                    {
                        stack.Add(name);
                        continue;
                    }
                    var value = type == 1 ? String(reader) : "";
                    if (type != 1)
                    {
                        file.Position += type switch { 2 or 3 or 4 or 6 => 4, 7 or 10 => 8, _ => throw new InvalidDataException("Unsupported appinfo value.") };
                    }

                    var launch = stack.FindIndex(item => item == "launch");
                    if (launch >= 0 && stack.Count > launch + 1)
                    {
                        var index = stack[launch + 1];
                        if (!launches.TryGetValue(index, out var fields))
                        {
                            launches[index] = fields = new Dictionary<string, string>();
                        }
                        fields[name] = value;
                    }
                }

                var candidates = launches.Values.Where(fields => fields.GetValueOrDefault("executable", "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        && (!fields.TryGetValue("oslist", out var os) || os.Split(',').Contains("windows")))
                    .Select(fields => Contained(root, Path.Combine(root, fields.GetValueOrDefault("workingdir", ""))))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return candidates.Length == 1 ? candidates[0] : "";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            // Missing, unsupported or ambiguous metadata means unavailable settings, not an invented directory.
        }

        return "";
    }

    private static string Contained(string root, string candidate)
    {
        if (!Path.IsPathFullyQualified(candidate))
        {
            return "";
        }
        var full = Path.GetFullPath(candidate);
        return Directory.Exists(full) && (StoragePaths.Same(root, full) || StoragePaths.IsUnder(root, full)) ? full : "";
    }

    private static string String(BinaryReader reader)
    {
        var bytes = new List<byte>();
        for (var length = 0; length < 32768; length++)
        {
            var value = reader.ReadByte();
            if (value == 0)
            {
                return Encoding.UTF8.GetString(bytes.ToArray());
            }
            bytes.Add(value);
        }

        throw new InvalidDataException("An appinfo string exceeds its limit.");
    }
}
