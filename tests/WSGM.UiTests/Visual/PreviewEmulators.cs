using WSGM.Core;
using WSGM.Shell;
using WSGM.UiTests.Fakes;

namespace WSGM.UiTests.Visual;

/// <summary>
///     The Emulators page's synthetic library: six installed emulators in the states the page distinguishes, the
///     catalogue around them, a BIOS folder, the library's counts and the RetroArch cores. Nothing is on disk.
/// </summary>
internal static class PreviewEmulators
{
    internal static EmulatorOverlaySource Create()
    {
        var psBios = Rule("bios", "PlayStation BIOS", true, "Supply a locally dumped PlayStation BIOS.");
        var ps2Bios = Rule("bios", "PlayStation 2 BIOS", true,
            "Supply a locally dumped PS2 BIOS and select it in PCSX2 when several are present.");
        var definitions = new[]
        {
            Definition("retroarch", "RetroArch", ["stable", "nightly"],
                ["nes", "snes", "gba", "psx", "megadrive", "saturn", "dreamcast", "arcade"],
                [Rule("system", "Core system files", false, "Supply the system files individual cores read.")],
                new EmulatorDataPolicy { HasCores = true }),
            Definition("dolphin", "Dolphin", ["release", "development"], ["gamecube", "wii"], []),
            Definition("pcsx2", "PCSX2", ["stable", "nightly"], ["ps2"], [ps2Bios],
                new EmulatorDataPolicy { RequiresVisualCpp = true }),
            Definition("duckstation", "DuckStation", ["latest", "preview"], ["psx"], [psBios]),
            Definition("cemu", "Cemu", ["release"], ["wiiu"],
                [Rule("keys", "Optional Wii U keys / account data", false, "Supply your own keys.txt.")]),
            Definition("ppsspp", "PPSSPP", ["release"], ["psp"], []),
            Definition("rpcs3", "RPCS3", ["rolling"], ["ps3"],
            [
                Rule("firmware", "PlayStation 3 firmware", true,
                    "Choose a local PS3UPDAT.PUP; RPCS3 applies it through its installer.")
            ]),
            Definition("xemu", "Xemu", ["release"], ["xbox"],
            [
                Rule("system", "Xbox boot ROM, flash BIOS and hard disk", true,
                    "Supply a local folder containing mcpx_1.0.bin, bios.bin and xbox_hdd.qcow2.")
            ]),
            Definition("flycast", "Flycast", ["release"], ["dreamcast", "naomi", "atomiswave"],
                [Rule("bios", "Dreamcast / arcade BIOS", false, "Supply your own dc_boot.bin and dc_flash.bin.")]),
            Definition("melonds", "melonDS", ["release"], ["nds"], []),
            Definition("mame", "MAME", ["release"], ["arcade"], []),
            Definition("eden", "Eden", ["stable", "nightly"], ["switch"],
            [
                Rule("keys", "Console decryption keys", true, "Supply your own prod.keys."),
                Rule("firmware", "Console firmware", true, "Supply your own firmware dump.")
            ], new EmulatorDataPolicy { RequiresVisualCpp = true })
        };
        EmulatorCore[] cores =
        [
            Core("mednafen_psx_hw_libretro", "Beetle PSX HW", ["psx"]),
            Core("genesis_plus_gx_libretro", "Genesis Plus GX", ["megadrive", "mastersystem", "gamegear"]),
            Core("mgba_libretro", "mGBA", ["gba", "gb", "gbc"]),
            Core("snes9x_libretro", "Snes9x", ["snes"]),
            Core("flycast_libretro", "Flycast", ["dreamcast", "naomi"]),
            Core("mednafen_saturn_libretro", "Beetle Saturn", ["saturn"]),
            Core("fbneo_libretro", "FinalBurn Neo", []) with { MetadataMissing = true }
        ];
        EmulatorInstallation[] installations =
        [
            Installed(definitions[0], "stable", "1.21.0", true) with { Cores = cores },
            Installed(definitions[1], "release", "2412", true),
            Installed(definitions[2], "nightly", "2.2.0", true) with
            {
                MissingRequirements = ["Configure PlayStation 2 BIOS. Supply a locally dumped PS2 BIOS."]
            },
            Installed(definitions[3], "latest", "0.1-8291", true),
            Installed(definitions[4], "release", "2.6", true),
            Installed(definitions[5], "release", "1.18.1", false)
        ];
        var source = new EmulatorOverlaySource
        {
            State = new EmulatorSnapshot
            {
                Initialized = true, Definitions = definitions, Installations = installations,
                BiosFolder = @"D:\Emulation\bios",
                Offers =
                [
                    Offer("retroarch", "stable", "1.22.0", "new"), Offer("dolphin", "release", "2412", "installed"),
                    Offer("pcsx2", "nightly", "2.2.0", "installed"),
                    Offer("duckstation", "latest", "0.1-8291", "installed"), Offer("cemu", "release", "2.7", "new"),
                    Offer("rpcs3", "rolling", "0.0.36", "r"), Offer("xemu", "release", "0.8.106", "r"),
                    Offer("flycast", "release", "2.4", "r"), Offer("melonds", "release", "1.0", "r"),
                    Offer("mame", "release", "0.281", "r"), Offer("eden", "stable", "0.0.4", "r"),
                    Offer("pcsx2", "stable", "2.4.1", "r")
                ],
                SystemPreferences =
                [
                    new EmulatorSystemPreference { SystemId = "psx", InstallationId = "duckstation" },
                    new EmulatorSystemPreference { SystemId = "ps2", InstallationId = "pcsx2" },
                    new EmulatorSystemPreference
                        { SystemId = "snes", InstallationId = "retroarch", CoreId = "snes9x_libretro" },
                    new EmulatorSystemPreference
                        { SystemId = "gba", InstallationId = "retroarch", CoreId = "mgba_libretro" },
                    new EmulatorSystemPreference
                        { SystemId = "dreamcast", InstallationId = "retroarch", CoreId = "flycast_libretro" }
                ]
            },
            Bios = new EmulatorBiosState(@"D:\Emulation\bios", true,
            [
                Bios("psx", "PlayStation", ["DuckStation", "RetroArch"], true,
                    File("scph5500.bin", "Missing"), File("scph5501.bin", "Verified"), File("scph5502.bin", "Missing")),
                Bios("ps2", "PlayStation 2", ["PCSX2"], true, File("SCPH-10000.bin", "Missing"),
                    File("SCPH-39001.bin", "Missing")),
                Bios("dreamcast", "Dreamcast", ["RetroArch"], false, File("dc/dc_boot.bin", "Missing"),
                    File("dc/dc_flash.bin", "Missing")),
                Bios("saturn", "Saturn", ["RetroArch"], true, File("sega_101.bin", "Missing"),
                    File("mpr-17933.bin", "Missing")),
                Bios("gba", "Game Boy Advance", ["RetroArch"], false, File("gba_bios.bin", "Verified", true)),
                Bios("ps3", "PlayStation 3", [], false, File("PS3UPDAT.PUP", "Missing"))
            ]),
            Usage = new EmulatorLibraryUsage(
                [
                    new EmulatorDependencyCount("pcsx2", 2, 48), new EmulatorDependencyCount("retroarch", 3, 188),
                    new EmulatorDependencyCount("duckstation", 1, 212)
                ],
                [
                    new EmulatorSystemUsage("psx", 1, 212), new EmulatorSystemUsage("ps2", 2, 48),
                    new EmulatorSystemUsage("snes", 1, 96), new EmulatorSystemUsage("gba", 1, 74),
                    new EmulatorSystemUsage("dreamcast", 1, 18), new EmulatorSystemUsage("ps3", 1, 9)
                ], new Dictionary<string, uint>()),
            CoreStatus =
            [
                new EmulatorCoreStatus("retroarch", "flycast_libretro", true)
                    { MissingFiles = ["dc_boot.bin", "dc_flash.bin"] },
                new EmulatorCoreStatus("retroarch", "mednafen_saturn_libretro", true)
                    { MissingFiles = ["sega_101.bin", "mpr-17933.bin"] }
            ],
            Setup = new Dictionary<string, EmulatorSetupItem[]>
            {
                ["pcsx2"] =
                [
                    new EmulatorSetupItem("bios", "PlayStation 2 BIOS", false,
                        @"Not found in D:\Emulation\bios. Supply a locally dumped PS2 BIOS.", true),
                    new EmulatorSetupItem("runtime", "Visual C++ runtime", true, "Installed", true)
                ],
                ["duckstation"] =
                [
                    new EmulatorSetupItem("bios", "PlayStation BIOS", true, @"Found in D:\Emulation\bios", true)
                ],
                ["retroarch"] =
                [
                    new EmulatorSetupItem("system", "Core system files", true, @"Found in D:\Emulation\bios", false)
                ]
            }
        };
        return source;
    }

    private static EmulatorPrerequisite Rule(string kind, string name, bool required, string description)
    {
        return new EmulatorPrerequisite { Kind = kind, Name = name, Required = required, Description = description };
    }

    private static EmulatorDefinition Definition(string id, string name, string[] channels, string[] systems,
        EmulatorPrerequisite[] prerequisites, EmulatorDataPolicy? policy = null)
    {
        return new EmulatorDefinition
        {
            Id = id, Name = name, Channels = channels, Systems = systems, Prerequisites = prerequisites,
            Source = "GitHub", DataPolicy = (policy ?? new EmulatorDataPolicy()) with { Prerequisites = prerequisites }
        };
    }

    private static EmulatorInstallation Installed(EmulatorDefinition definition, string channel, string version,
        bool managed)
    {
        return new EmulatorInstallation
        {
            Id = definition.Id, DefinitionId = definition.Id, Name = definition.Name, Channel = channel,
            Managed = managed, Version = version, Architecture = "x64", Source = "GitHub", ReleaseId = "installed",
            Systems = definition.Systems, Integrity = "SHA-256 matches the publisher",
            Root = @"D:\Emulators\" + definition.Name, ExecutablePath = @"D:\Emulators\" + definition.Name + @"\"
                                                                        + definition.Id + ".exe",
            DataPath = @"D:\EmulatorData\" + definition.Id, DataPolicy = definition.DataPolicy
        };
    }

    private static EmulatorCore Core(string id, string name, string[] systems)
    {
        return new EmulatorCore
            { Id = id, Name = name, Systems = systems, Path = @"D:\Emulators\RetroArch\cores\" + id + ".dll" };
    }

    private static EmulatorOffer Offer(string definition, string channel, string version, string release)
    {
        return new EmulatorOffer
        {
            DefinitionId = definition, Channel = channel, Version = version, ReleaseId = release,
            NotesUrl = "https://example.invalid/notes"
        };
    }

    private static EmulatorBiosSystem Bios(string id, string name, string[] emulators, bool any,
        params EmulatorBiosFile[] files)
    {
        return new EmulatorBiosSystem(id, name, "", emulators, files, []) { Any = any };
    }

    private static EmulatorBiosFile File(string path, string status, bool optional = false)
    {
        return new EmulatorBiosFile(path, status, status == "Verified" ? "example checksum" : "", optional);
    }
}
