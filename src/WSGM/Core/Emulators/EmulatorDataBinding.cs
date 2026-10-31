using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>Binds a managed emulator's own configuration file to the data folder WSGM gave it.</summary>
internal static class EmulatorDataBinding
{
    /// <summary>Writes the installation's data paths into the emulator's configuration file.</summary>
    /// <param name="installed">The installation; an external one, or one without configured paths, is left alone.</param>
    /// <remarks>The emulator is stopped first when the file names other paths, so it cannot write its old ones back.</remarks>
    internal static void Configure(EmulatorInstallation installed)
    {
        if (!installed.Managed)
        {
            return;
        }

        if (installed.DataPolicy.ConfigPaths.Count == 0)
        {
            return;
        }

        var root = Path.GetDirectoryName(installed.ExecutablePath)!;
        var fields = installed.DataPolicy.ConfigPaths.ToDictionary(pair => pair.Key, pair =>
            pair.Value.Replace("{program}", root, StringComparison.Ordinal)
                .Replace("{data}", installed.DataPath, StringComparison.Ordinal));
        var configPath = Path.Combine(installed.DataPath, installed.DataPolicy.ConfigFile);
        if (fields.Any(pair =>
                IniFile.ReadValue(configPath, pair.Key)?.Trim('"').Replace('/', Path.DirectorySeparatorChar) !=
                pair.Value) ||
            (installed.DefinitionId == "retroarch" &&
             EmulatorPortable.RetroArchFlags.Any(key => IniFile.ReadValue(configPath, key) != "false")))
        {
            EmulatorPrerequisites.EnsureStopped(installed);
        }

        foreach (var (key, value) in fields)
        {
            var fileSetting = key is "core_options_path" or "content_history_path" or "content_favorites_path"
                or "content_image_history_path" or "content_music_history_path" or "content_video_history_path";
            Directory.CreateDirectory(fileSetting ? Path.GetDirectoryName(value)! : value);
        }

        IniFile.SetValues(Path.Combine(installed.DataPath, installed.DataPolicy.ConfigFile), fields.ToDictionary(
            pair => pair.Key, pair => string.Concat((char)34,
                pair.Value.Replace(Path.DirectorySeparatorChar, '/'), (char)34)));
        if (installed.DefinitionId == "retroarch")
        {
            IniFile.SetValues(Path.Combine(installed.DataPath, installed.DataPolicy.ConfigFile),
                EmulatorPortable.RetroArchFlags.ToDictionary(key => key, _ => "false"));
        }
    }

    /// <summary>Moves supplied prerequisite paths that lived in an old data folder into the new one.</summary>
    /// <param name="prerequisites">The configured prerequisites by kind.</param>
    /// <param name="previousData">The data folder they were supplied under.</param>
    /// <param name="data">The data folder that replaced it.</param>
    /// <returns>The prerequisites; paths outside the old data folder are kept as they are.</returns>
    internal static Dictionary<string, string> Rebase(Dictionary<string, string> prerequisites, string previousData,
        string data)
    {
        return prerequisites.ToDictionary(pair => pair.Key,
            pair => Path.IsPathFullyQualified(pair.Value) && StoragePaths.IsUnder(previousData, pair.Value)
                ? Path.Combine(data, Path.GetRelativePath(previousData, pair.Value))
                : pair.Value);
    }
}
