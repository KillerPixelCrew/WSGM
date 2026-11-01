using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class EmulatorPageProjectionTests
{
    [Fact]
    public void SystemRowsCountGamesAndSourcesAndFlagGamesWithoutAnEmulator()
    {
        var snapshot = Snapshot([Installed("duckstation", ["psx"])], []);
        var rows = EmulatorPageProjection.Systems(snapshot, RomProfiles.All,
        [
            new EmulatorSystemUsage("psx", 1, 212), new EmulatorSystemUsage("ps2", 2, 48),
            new EmulatorSystemUsage("snes", 1, null), new EmulatorSystemUsage("saturn", 1, 0)
        ]).ToDictionary(row => row.SystemId);

        Assert.Equal("212 games from 1 source", rows["psx"].Summary);
        Assert.False(rows["psx"].NeedsEmulator);
        Assert.True(rows["ps2"].NeedsEmulator);
        Assert.Equal("48 games have no emulator yet", rows["ps2"].Summary);
        Assert.Equal("1 source has no emulator yet", rows["saturn"].Summary);
        Assert.Equal("1 source · imported games could not be counted",
            EmulatorPageProjection.SystemSummary(1, null, false));
        Assert.False(rows["nes"].InLibrary);
        Assert.Equal("No games yet", rows["nes"].Summary);
        Assert.Equal("PlayStation", rows["ps2"].Family);
    }

    [Fact]
    public void AFrontendRunsOnlyTheSystemsItsCoresName()
    {
        var retroarch = Installed("retroarch", []) with
        {
            DataPolicy = new EmulatorDataPolicy { HasCores = true },
            Cores = [new EmulatorCore { Id = "snes9x", Name = "Snes9x", Systems = ["snes"] }]
        };
        var rows = EmulatorPageProjection.Systems(Snapshot([retroarch], []), RomProfiles.All,
                [new EmulatorSystemUsage("snes", 1, 4), new EmulatorSystemUsage("ps3", 1, 9)])
            .ToDictionary(row => row.SystemId);

        Assert.False(rows["snes"].NeedsEmulator);
        Assert.True(rows["ps3"].NeedsEmulator);
        Assert.Equal("9 games have no emulator yet", rows["ps3"].Summary);
    }

    [Fact]
    public void BiosSummaryCountsNeededFilesOfInstalledSystemsAndTreatsAlternativesAsOne()
    {
        var summary = EmulatorPageProjection.Bios(new EmulatorBiosState(@"D:\BIOS", true,
        [
            new EmulatorBiosSystem("psx", "PlayStation", "Verified", ["DuckStation"],
                [File("scph5500.bin", "Missing"), File("scph5501.bin", "Verified")], []) { Any = true },
            new EmulatorBiosSystem("ps2", "PlayStation 2", "Missing", ["PCSX2"],
                [File("SCPH-10000.bin", "Missing"), File("SCPH-39001.bin", "Missing")], []) { Any = true },
            new EmulatorBiosSystem("dreamcast", "Dreamcast", "Partial", ["RetroArch"],
                [File("dc/dc_boot.bin", "Present"), File("dc/dc_flash.bin", "Wrong file")], []),
            new EmulatorBiosSystem("gba", "Game Boy Advance", "Optional", ["RetroArch"],
                [File("gba_bios.bin", "Missing", true)], []),
            new EmulatorBiosSystem("ps3", "PlayStation 3", "Missing", [], [File("PS3UPDAT.PUP", "Missing")], [])
        ]));

        Assert.Equal(2, summary.Found);
        Assert.Equal(4, summary.Needed);
        Assert.Equal("2 of 4 files found", summary.Label);
        var systems = summary.Systems.ToDictionary(system => system.SystemId);
        var psx = Assert.Single(systems["psx"].Items);
        Assert.Equal(("scph5501.bin", "verified", "Verified"), (psx.Label, psx.State, psx.StateLabel));
        var ps2 = Assert.Single(systems["ps2"].Items);
        Assert.Equal(("Any PlayStation 2 BIOS (for example SCPH-10000.bin)", "missing"), (ps2.Label, ps2.State));
        Assert.Equal(["present", "wrongFile"], systems["dreamcast"].Items.Select(item => item.State));
        Assert.True(systems["gba"].Optional);
        Assert.Equal("ps3", summary.Systems[^1].SystemId);
        Assert.Equal("", systems["ps3"].UsedBy);
        Assert.Equal("Not checked yet", EmulatorPageProjection.Bios(new EmulatorBiosState("", false, [])).Label);
    }

    [Fact]
    public void CatalogueStatesRequirementsFromPrerequisitesAndGroupsByFamily()
    {
        var snapshot = Snapshot([Installed("dolphin", ["gamecube", "wii"]) with { Version = "2412" }],
            [
                Definition("rpcs3", "RPCS3", ["ps3"],
                    [Rule("firmware", "PlayStation 3 firmware", true), Rule("system", "Optional data", false)]),
                Definition("flycast", "Flycast", ["dreamcast", "naomi"], [Rule("bios", "Dreamcast BIOS", false)]),
                Definition("eden", "Eden", ["switch"],
                    [Rule("keys", "Console decryption keys", true), Rule("firmware", "Console firmware", true)]),
                Definition("dolphin", "Dolphin", ["gamecube", "wii"], [])
            ]) with
            {
                Offers =
                [
                    new EmulatorOffer
                        { DefinitionId = "rpcs3", Channel = "stable", Version = "0.0.36", ReleaseId = "r" }
                ]
            };
        var catalog = EmulatorPageProjection.Catalog(snapshot).ToDictionary(item => item.DefinitionId);

        Assert.Equal(["dolphin", "eden", "flycast", "rpcs3"], EmulatorPageProjection.Catalog(snapshot)
            .Select(item => item.DefinitionId));
        Assert.Equal("Needs after install: PlayStation 3 firmware", catalog["rpcs3"].Requirement);
        Assert.Equal("Needs after install: Console decryption keys and Console firmware", catalog["eden"].Requirement);
        Assert.Equal("", catalog["flycast"].Requirement);
        Assert.Equal("0.0.36", catalog["rpcs3"].LatestVersion);
        Assert.Equal("2412", catalog["dolphin"].InstalledVersion);
        Assert.Equal(["Sega", "Arcade"], catalog["flycast"].Families);
        Assert.Equal(["Nintendo"], catalog["eden"].Families);
    }

    [Fact]
    public void StatusChipsShowUpdateSetupAndOwnershipFromState()
    {
        var bios = Rule("bios", "PlayStation 2 BIOS", true);
        var pcsx2 = Installed("pcsx2", ["ps2"]) with
        {
            DataPolicy = new EmulatorDataPolicy { Prerequisites = [bios] }
        };
        var dolphin = Installed("dolphin", ["gamecube", "wii"]);
        var ppsspp = Installed("ppsspp", ["psp"]) with { Managed = false };
        var cemu = Installed("cemu", ["wiiu"]);
        var neverChecked = Installed("mgba", ["gba"]);
        var snapshot = Snapshot([pcsx2, dolphin, ppsspp, cemu, neverChecked], []) with
        {
            Offers =
            [
                Offer("pcsx2", "2.3.0", "new"), Offer("dolphin", "2412", "installed"), Offer("cemu", "2.7", "") with
                {
                    Error = "The release feed is unavailable."
                }
            ]
        };
        EmulatorSetupItem[] missing = [new("bios", "PlayStation 2 BIOS", false, "Supply it.", true)];

        var pcsx2Item = EmulatorPageProjection.Installed(snapshot, pcsx2, missing, 42);
        Assert.Equal([("update", "Update to 2.3.0", "action"), ("setup", "BIOS missing", "warn")],
            pcsx2Item.Chips.Select(chip => (chip.Kind, chip.Label, chip.Tone)));
        Assert.Equal("1 file missing", pcsx2Item.SetupLabel);
        Assert.Equal("2.3.0", pcsx2Item.UpdateVersion);
        Assert.Equal(42u, pcsx2Item.AppId);
        Assert.Equal(["upToDate"],
            EmulatorPageProjection.Installed(snapshot, dolphin, [], 0).Chips.Select(chip => chip.Kind));
        var external = EmulatorPageProjection.Installed(snapshot, ppsspp, [], 0);
        Assert.Equal(["external"], external.Chips.Select(chip => chip.Kind));
        Assert.Equal("1.0 · your own installation", external.VersionLine);
        Assert.Equal(["checkFailed"],
            EmulatorPageProjection.Installed(snapshot, cemu, [], 0).Chips.Select(chip => chip.Kind));
        Assert.Equal(["notChecked"],
            EmulatorPageProjection.Installed(snapshot, neverChecked, [], 0).Chips.Select(chip => chip.Kind));
        var skipped = pcsx2 with { IgnoredReleaseId = "new" };
        Assert.Equal(["setup"], EmulatorPageProjection.Installed(snapshot with { Installations = [skipped] }, skipped,
            missing, 0).Chips.Select(chip => chip.Kind));
        var runtime = EmulatorPageProjection.Installed(snapshot, dolphin,
            [new EmulatorSetupItem("runtime", "Visual C++ runtime", false, "Install it.", true)], 0);
        Assert.Equal("Runtime missing", runtime.Chips.Single(chip => chip.Kind == "setup").Label);
        Assert.Equal("1 item missing", runtime.SetupLabel);
    }

    [Fact]
    public void PendingUpdatesListManagedNewerReleasesThatWereNotSkipped()
    {
        var snapshot = Snapshot(
            [
                Installed("retroarch", ["snes"]) with { Name = "RetroArch" }, Installed("cemu", ["wiiu"]) with
                {
                    Name = "Cemu"
                },
                Installed("pcsx2", ["ps2"]) with { IgnoredReleaseId = "new" },
                Installed("ppsspp", ["psp"]) with { Managed = false }
            ], []) with
            {
                Offers =
                [
                    Offer("retroarch", "1.22.0", "new"), Offer("cemu", "2.7", "new"), Offer("pcsx2", "2.3.0", "new"),
                    Offer("ppsspp", "1.19", "new")
                ]
            };
        var updates = EmulatorPageProjection.PendingUpdates(snapshot);

        Assert.Equal(["retroarch", "cemu"], updates.Select(update => update.InstallationId));
        Assert.Equal("2 updates ready: RetroArch 1.22.0 and Cemu 2.7", EmulatorPageProjection.UpdatesSummary(updates));
        Assert.Equal("1 update ready: RetroArch 1.22.0", EmulatorPageProjection.UpdatesSummary(updates[..1]));
        Assert.Equal("", EmulatorPageProjection.UpdatesSummary([]));
    }

    private static EmulatorSnapshot Snapshot(EmulatorInstallation[] installations, EmulatorDefinition[] definitions)
    {
        return new EmulatorSnapshot { Initialized = true, Installations = installations, Definitions = definitions };
    }

    private static EmulatorInstallation Installed(string id, string[] systems)
    {
        return new EmulatorInstallation
        {
            Id = id, DefinitionId = id, Name = id, Channel = "stable", Version = "1.0", Managed = true,
            ReleaseId = "installed", Systems = systems
        };
    }

    private static EmulatorDefinition Definition(string id, string name, string[] systems,
        EmulatorPrerequisite[] prerequisites)
    {
        return new EmulatorDefinition
            { Id = id, Name = name, Channels = ["stable"], Systems = systems, Prerequisites = prerequisites };
    }

    private static EmulatorPrerequisite Rule(string kind, string name, bool required)
    {
        return new EmulatorPrerequisite { Kind = kind, Name = name, Required = required };
    }

    private static EmulatorOffer Offer(string id, string version, string release)
    {
        return new EmulatorOffer { DefinitionId = id, Channel = "stable", Version = version, ReleaseId = release };
    }

    private static EmulatorBiosFile File(string path, string status, bool optional = false)
    {
        return new EmulatorBiosFile(path, status, "", optional);
    }
}
