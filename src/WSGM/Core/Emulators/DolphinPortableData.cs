using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace WSGM.Core;

/// <summary>Dolphin's GBA saves kept beside their ROMs, preserved into portable data before rebinding.</summary>
internal static class DolphinPortableData
{
    /// <summary>Reads the GBA save files Dolphin keeps beside their ROMs.</summary>
    /// <param name="installed">Installation whose Dolphin.ini is read.</param>
    /// <returns>The save paths, empty unless saves live beside ROMs.</returns>
    internal static IEnumerable<string> ReadGbaSaves(EmulatorInstallation installed)
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
            EmulatorPortableSetup.RequireRomMedia(rom);
            yield return Path.ChangeExtension(rom, null) + "-" + slot + ".sav";
        }
    }

    /// <summary>Copies the GBA saves into portable data and points Dolphin at them.</summary>
    /// <param name="installed">Installation whose data path is already the portable data.</param>
    /// <param name="saves">Saves read before the data moved.</param>
    /// <param name="token">Cancels the copy.</param>
    internal static void PreserveGbaSaves(EmulatorInstallation installed, IReadOnlyList<string> saves,
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
            EmulatorPortableSetup.CopyPath(save, Path.Combine(destination, Path.GetFileName(save)), token);
        }

        IniFile.SetValue(config, "SavesPath", destination.Replace('\\', '/'), "GBA");
        IniFile.SetValue(config, "SavesInRomPath", "False", "GBA");
    }
}
