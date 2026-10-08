using WSGM.Core;
using WSGM.UiTests.Fakes;

namespace WSGM.UiTests.Visual;

internal static class PreviewEmulators
{
    internal static EmulatorOverlaySource Create()
    {
        var bios = new EmulatorPrerequisite
            { Kind = "bios", Name = "Console BIOS", Description = "Supply a local console dump." };
        var definitions = new[]
        {
            new EmulatorDefinition
            {
                Id = "retroarch", Name = "RetroArch (all cores)", Channels = ["stable"],
                Systems = ["nes", "snes", "psx", "ps2", "saturn"],
                DataPolicy = new EmulatorDataPolicy { HasCores = true }
            },
            new EmulatorDefinition
            {
                Id = "pcsx2", Name = "PCSX2", Channels = ["stable", "nightly"], Systems = ["ps2"],
                Prerequisites = [bios]
            },
            new EmulatorDefinition
            {
                Id = "duckstation", Name = "DuckStation", Channels = ["latest"], Systems = ["psx"],
                Prerequisites = [bios]
            },
            new EmulatorDefinition
                { Id = "dolphin", Name = "Dolphin", Channels = ["release"], Systems = ["gamecube", "wii"] },
            new EmulatorDefinition { Id = "rpcs3", Name = "RPCS3", Channels = ["rolling"], Systems = ["ps3"] },
            new EmulatorDefinition
                { Id = "eden", Name = "Eden", Channels = ["stable"], Systems = ["switch"], Prerequisites = [bios] }
        };
        var installations = definitions.Take(4).Select((definition, index) => new EmulatorInstallation
        {
            Id = definition.Id, DefinitionId = definition.Id, Name = definition.Name, Channel = definition.Channels[0],
            Managed = index != 3, Version = index == 1 ? "v2.4.0" : index == 0 ? "1.21.0" : "2506a",
            Architecture = "x64", Source = "Scoop", ReleaseId = "installed", Systems = definition.Systems,
            Integrity = "SHA-256 matches the publisher", ExecutablePath = @"D:\Emulators\" + definition.Id + ".exe",
            DataPath = @"D:\EmulatorData\" + definition.Id, DataPolicy = definition.DataPolicy,
            MissingRequirements = index == 1 ? ["PlayStation 2 BIOS missing"] : [],
            Cores = index == 0
                ?
                [
                    new EmulatorCore { Id = "snes9x", Name = "Snes9x", Systems = ["snes"], Extensions = [".sfc"] },
                    new EmulatorCore { Id = "bsnes", Name = "bsnes", Systems = ["snes"], Extensions = [".sfc"] },
                    new EmulatorCore
                        { Id = "beetle_saturn", Name = "Beetle Saturn", Systems = ["saturn"], MetadataMissing = true }
                ]
                : []
        }).ToArray();
        return new EmulatorOverlaySource
        {
            State = new EmulatorSnapshot
            {
                Initialized = true, Definitions = definitions, Installations = installations,
                Offers =
                [
                    new EmulatorOffer
                    {
                        DefinitionId = "pcsx2", Channel = "stable", Version = "v2.4.1", ReleaseId = "new",
                        NotesUrl = "https://example.invalid/notes"
                    }
                ],
                SystemPreferences =
                [
                    new EmulatorSystemPreference { SystemId = "snes", InstallationId = "retroarch", CoreId = "snes9x" },
                    new EmulatorSystemPreference { SystemId = "ps2", InstallationId = "pcsx2" }
                ]
            }
        };
    }
}
