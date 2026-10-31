using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>The profiles the importer exposes instead of asking surfaces to invent command lines.</summary>
/// <param name="Id">The emulated system identity.</param>
/// <param name="Name">The user-visible system name.</param>
/// <param name="Extensions">The default accepted file extensions.</param>
/// <param name="ScreenscraperSystemId">The provider's system identity, or zero when unmapped.</param>
/// <param name="ParserKind">The matching behavior determined by this system.</param>
public sealed record RomSystemProfile(
    string Id,
    string Name,
    IReadOnlyList<string> Extensions,
    int ScreenscraperSystemId,
    RomParserKind ParserKind);

/// <summary>The actual entry-point parsing required by a system's supported launch formats.</summary>
public enum RomParserKind
{
    /// <summary>An individual ROM file.</summary>
    File,

    /// <summary>A disc image or descriptor with required companions.</summary>
    Disc,

    /// <summary>A verified PlayStation 3 extracted/installed EBOOT entry point.</summary>
    Eboot,

    /// <summary>An installed Vita/PS4 application selected by its eboot.bin entry point.</summary>
    InstalledEboot
}

/// <summary>The reviewed standalone profiles plus explicit installed-core system metadata.</summary>
public static class RomProfiles
{
    /// <summary>Recognizes system folders beneath a collection root, or the selected system folder itself.</summary>
    public static RomSystemProfile Automatic { get; } = new("auto", "Detect system folders", [], 0, RomParserKind.File);

    /// <summary>The reviewed first-release system profiles.</summary>
    public static IReadOnlyList<RomSystemProfile> All { get; } =
    [
        new("nes", "Nintendo Entertainment System", [".nes", ".fds", ".zip", ".7z"], 3,
            RomParserKind.File),
        new("snes", "Super Nintendo", [".sfc", ".smc", ".zip", ".7z"], 4, RomParserKind.File),
        new("gb", "Game Boy", [".gb", ".zip", ".7z"], 9, RomParserKind.File),
        new("gbc", "Game Boy Color", [".gbc", ".zip", ".7z"], 10, RomParserKind.File),
        new("gba", "Game Boy Advance", [".gba", ".zip", ".7z"], 12, RomParserKind.File),
        new("n64", "Nintendo 64", [".n64", ".z64", ".v64", ".zip", ".7z"], 14, RomParserKind.File),
        new("nds", "Nintendo DS", [".nds", ".zip"], 15, RomParserKind.File),
        new("megadrive", "Mega Drive / Genesis", [".md", ".gen", ".bin", ".zip", ".7z"], 1,
            RomParserKind.File),
        new("mastersystem", "Sega Master System", [".sms", ".zip", ".7z"], 2, RomParserKind.File),
        new("gamegear", "Sega Game Gear", [".gg", ".zip", ".7z"], 21, RomParserKind.File),
        new("sega32x", "Sega 32X", [".32x", ".bin", ".zip", ".7z"], 19, RomParserKind.File),
        new("pico", "Sega Pico", [".md", ".bin", ".zip", ".7z"], 0, RomParserKind.File),
        new("saturn", "Sega Saturn", [".cue", ".chd", ".m3u"], 22, RomParserKind.Disc),
        new("dreamcast", "Sega Dreamcast", [".gdi", ".cdi", ".chd", ".m3u"], 23, RomParserKind.Disc),
        new("psp", "PlayStation Portable", [".iso", ".cso", ".pbp", ".chd"], 13, RomParserKind.File),
        new("psx", "PlayStation", [".cue", ".chd", ".pbp", ".m3u"], 57, RomParserKind.Disc),
        new("ps2", "PlayStation 2", [".iso", ".chd", ".cso", ".bin", ".img", ".mdf", ".gz", ".m3u"], 58,
            RomParserKind.Disc),
        new("ps3", "PlayStation 3", [".bin"], 59, RomParserKind.Eboot),
        new("gamecube", "Nintendo GameCube", [".iso", ".gcm", ".gcz", ".rvz", ".wia", ".ciso", ".dol", ".elf"],
            64, RomParserKind.File),
        new("wii", "Nintendo Wii", [".iso", ".wbfs", ".gcz", ".rvz", ".wia", ".ciso", ".wad", ".dol", ".elf"],
            16, RomParserKind.File),
        new("switch", "Nintendo Switch", [".xci", ".nsp", ".nca", ".nro", ".nso", ".nsz", ".xcz"], 225,
            RomParserKind.File),
        new("3ds", "Nintendo 3DS", [".3ds", ".cci", ".cxi", ".app"], 17, RomParserKind.File),
        new("wiiu", "Nintendo Wii U", [".wua", ".wud", ".wux", ".rpx"], 18, RomParserKind.File),
        new("xbox", "Xbox", [".iso"], 32, RomParserKind.File),
        new("xbox360", "Xbox 360", [".iso", ".xex"], 33, RomParserKind.File),
        new("psvita", "PlayStation Vita (installed apps)", [".bin"], 62, RomParserKind.InstalledEboot),
        new("ps4", "PlayStation 4 (extracted games)", [".bin"], 60, RomParserKind.InstalledEboot),
        new("jaguar", "Atari Jaguar", [".j64", ".jag", ".rom", ".abs", ".cof", ".bin"], 27,
            RomParserKind.File),
        new("jaguarcd", "Atari Jaguar CD", [".cdi", ".cue", ".bigpimg"], 171, RomParserKind.Disc),
        new("arcade", "Arcade / MAME", [".zip", ".7z"], 75, RomParserKind.File),
        new("naomi", "Sega NAOMI", [".zip", ".7z", ".dat", ".lst"], 56, RomParserKind.File),
        new("atomiswave", "Sammy Atomiswave", [".zip", ".7z"], 53, RomParserKind.File),
        new("model2", "Sega Model 2", [".zip"], 54, RomParserKind.File),
        new("model3", "Sega Model 3", [".zip"], 55, RomParserKind.File),
        new("scummvm", "ScummVM (game folders)", [".scummvm"], 123, RomParserKind.File),
        new("pcengine", "PC Engine / TurboGrafx-16", [".pce", ".zip", ".7z"], 31, RomParserKind.File)
    ];

    /// <summary>Finds a reviewed system profile by its identity.</summary>
    public static RomSystemProfile? Find(string id)
    {
        return All.FirstOrDefault(profile => profile.Id == EmulatorStorage.NormalizeSystemId(id));
    }

    /// <summary>Builds profiles from an already-loaded manager snapshot without file I/O.</summary>
    public static IReadOnlyList<RomSystemProfile> ForInstallations(IEnumerable<EmulatorInstallation> installations)
    {
        List<RomSystemProfile> profiles = [.. All];
        foreach (var core in installations.SelectMany(installation => installation.Cores))
        {
            foreach (var system in core.Systems)
            {
                var systemId = EmulatorStorage.NormalizeSystemId(system);
                var existing = profiles.FindIndex(profile =>
                    profile.Id.Equals(systemId, StringComparison.OrdinalIgnoreCase));
                var extensions =
                    core.Extensions.Select(extension => extension.StartsWith('.') ? extension : "." + extension);
                if (existing < 0)
                {
                    profiles.Add(new RomSystemProfile(systemId, system,
                        [.. extensions.Distinct(StringComparer.OrdinalIgnoreCase)],
                        0, RomParserKind.File));
                }
                else
                {
                    profiles[existing] = profiles[existing] with
                    {
                        Extensions =
                        [.. profiles[existing].Extensions.Concat(extensions).Distinct(StringComparer.OrdinalIgnoreCase)]
                    };
                }
            }
        }

        return profiles;
    }
}
