using System;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>Native Windows portable layouts for the additional EmuDeck emulators.</summary>
internal static class EmulatorPortableProfiles
{
    internal static bool Supports(string id)
    {
        return id is "mgba" or "melonds" or "ppsspp" or "flycast" or "cemu" or "azahar"
            or "vita3k" or "xemu" or "xenia" or "mame" or "supermodel" or "model2"
            or "bigpemu" or "ares" or "rmg" or "primehack" or "scummvm" or "shadps4" or "citron"
            or "ryujinx" or "suyu" or "armsx2";
    }

    internal static string Alias(string id)
    {
        return id switch
        {
            "azahar" => "user",
            "citron" => "user",
            "suyu" => "user",
            "ryujinx" => "portable",
            "cemu" => "portable",
            "vita3k" => "portable",
            "primehack" => "User",
            "shadps4" => "user",
            "bigpemu" => "UserData",
            _ => ""
        };
    }

    internal static string[] Markers(string id)
    {
        return id switch
        {
            "mgba" => ["portable.ini"],
            "melonds" => ["melonDS.toml"],
            "ppsspp" => ["memstick"],
            "flycast" => ["emu.cfg"],
            "cemu" => ["portable"],
            "xemu" => ["xemu.toml"],
            "xenia" => ["portable.txt"],
            "ares" => ["settings.bml"],
            "rmg" => ["Portable.txt"],
            "armsx2" => ["portable.txt"],
            "primehack" => ["portable.txt", "User"],
            "bigpemu" => ["UserData"],
            "scummvm" => ["scummvm.ini"],
            _ when Alias(id).Length > 0 => [Alias(id)],
            _ => []
        };
    }

    internal static string ReadExternal(string id, string program)
    {
        if (!Supports(id) || Markers(id).Any(marker => !File.Exists(Path.Combine(program, marker)) &&
                                                     !Directory.Exists(Path.Combine(program, marker))))
        {
            throw new InvalidDataException("Use " + id + "'s native portable mode and copy existing user data " +
                                           "into the installation folder before registering it.");
        }

        if (id == "ppsspp" && File.Exists(Path.Combine(program, "installed.txt")))
        {
            throw new InvalidDataException("Remove PPSSPP's installed.txt only after migrating its memory stick into memstick.");
        }

        if (id == "mgba")
        {
            var redirect = IniFile.ReadValue(Path.Combine(program, "portable.ini"), "path", "portable");
            if (!string.IsNullOrWhiteSpace(redirect) && redirect.Trim('"') != ".")
            {
                throw new InvalidDataException("mGBA portable.ini must use the installation folder before registering it.");
            }
        }

        if (id == "armsx2" && File.ReadAllText(Path.Combine(program, "portable.txt")).Trim() is not ("" or "."))
        {
            throw new InvalidDataException("ARMSX2 portable.txt must use adjacent data before registering this installation.");
        }

        return Path.Combine(program, Alias(id));
    }

    internal static void Verify(EmulatorInstallation installed)
    {
        var program = Path.GetDirectoryName(installed.ExecutablePath)!;
        var actual = ReadExternal(installed.DefinitionId, program);
        if (!Path.GetFullPath(EmulatorPortable.PhysicalPath(actual)).Equals(
                Path.GetFullPath(EmulatorPortable.PhysicalPath(installed.DataPath)), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The portable " + installed.Name + " data binding changed. Use Repair.");
        }
    }
}
