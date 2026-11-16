using System.Text.Json;
using System.Xml.Linq;
using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core;

public sealed class EsDeConfigurationTests
{
    private const string Program = @"C:\Users\A Player\WSGM\Emulators";

    [Fact]
    public void SystemDefaultComesFirstAndCommandsQuoteWsgmsOwnArguments()
    {
        var duckstation = Standalone("duckstation", "DuckStation", ["psx"],
            ["-batch", "-fullscreen", "--", "{rom}"]);
        var retroarch = RetroArch(new EmulatorCore
        {
            Id = "mednafen_psx_hw_libretro", Name = "Beetle PSX HW", Systems = ["psx"],
            Path = Program + @"\retroarch\cores\mednafen_psx_hw_libretro.dll"
        });
        var store = new EmulatorStore
        {
            Installations = [duckstation, retroarch],
            SystemPreferences =
            [
                new EmulatorSystemPreference
                    { SystemId = "psx", InstallationId = "retroarch", CoreId = "mednafen_psx_hw_libretro" }
            ]
        };

        var commands = EsDeConfiguration.Commands(store, "psx", out var excluded);

        Assert.Empty(excluded);
        Assert.Equal(["Beetle PSX HW", "DuckStation"], commands.Select(command => command.Label));
        Assert.Equal(
            $"\"{Program}\\retroarch\\retroarch.exe\" %STARTDIR%=\"{Program}\\retroarch\" -c "
            + $"\"{Program}\\retroarch-data\\retroarch.cfg\" -L \"{Program}\\retroarch\\cores\\mednafen_psx_hw_libretro.dll\" "
            + "-f %ROM%",
            commands[0].Command);
        Assert.Equal(
            $"\"{Program}\\duckstation\\duckstation.exe\" %STARTDIR%=\"{Program}\\duckstation\" -batch -fullscreen -- %ROM%",
            commands[1].Command);
    }

    [Fact]
    public void GameVariablesInsideAnArgumentUseTheUnescapedFormsInQuotes()
    {
        var mame = Standalone("mame", "MAME", ["arcade"],
            ["-rompath", "{romdir};{data}/bios", "{romname}", "-skip_gameinfo"]);

        var command = EsDeConfiguration.CommandLine(mame, null);

        Assert.EndsWith($"-rompath \"%GAMEDIRRAW%;{Program}\\mame-data/bios\" \"%BASENAME%\" -skip_gameinfo",
            command);
    }

    [Fact]
    public void EmulatorsWithoutAnEsDeFormAreLeftOutAndNamed()
    {
        var model2 = Standalone("model2", "Model 2 Emulator", ["model2"], ["{rom}"]);
        var percent = Standalone("cemu", "Cemu", ["wiiu"], ["-g", "{rom}"]) with
        {
            ExecutablePath = @"C:\100%\Cemu\Cemu.exe"
        };
        var store = new EmulatorStore { Installations = [model2, percent] };

        Assert.Empty(EsDeConfiguration.Commands(store, "model2", out var model2Excluded));
        Assert.Equal(["Model 2 Emulator"], model2Excluded);
        Assert.Empty(EsDeConfiguration.Commands(store, "wiiu", out _));
    }

    [Fact]
    public void PlanMapsNamesKeepsLayoutAndExplainsWhatEsDeLeavesOut()
    {
        var store = new EmulatorStore
        {
            Installations =
            [
                Standalone("dolphin", "Dolphin", ["gamecube"], ["-b", "-e", "{rom}"]),
                Standalone("pcsx2", "PCSX2", ["ps2"], ["-batch", "--", "{rom}"]),
                Standalone("rpcs3", "RPCS3", ["ps3"], ["--no-gui", "{rom}"])
            ]
        };
        EsDeFolder[] folders =
        [
            new("a", "Cube games", "gamecube", @"D:\Games\GC", [".iso", ".rvz"]),
            new("b", "PS2 card", "ps2", @"E:\PS2", [".iso", ".m3u"]),
            new("c", "PS2/home", "ps2", "", [".chd"]),
            new("d", "PS3", "ps3", @"D:\PS3", [".bin"]),
            new("e", "Snes", "snes", @"D:\SNES", [".sfc"])
        ];
        var bundled = new Dictionary<string, EsDeBundledSystem>
        {
            ["gc"] = new("gc", "Nintendo GameCube", "gc", "gc"),
            ["ps2"] = new("ps2", "Sony PlayStation 2", "ps2", "ps2"),
            ["ps3"] = new("ps3", "Sony PlayStation 3", "ps3", "ps3"),
            ["snes"] = new("snes", "Nintendo SNES", "snes", "snes")
        };

        var plan = EsDeConfiguration.Build(folders, store, RomProfiles.All, bundled);

        var gc = plan.Systems.Single(system => system.SystemId == "gamecube");
        Assert.True(gc.Shown);
        Assert.Equal("gc", gc.Name);
        Assert.Equal([".iso", ".ISO", ".rvz", ".RVZ"], gc.Extensions);
        var ps2 = plan.Systems.Single(system => system.SystemId == "ps2");
        Assert.DoesNotContain(".m3u", ps2.Extensions);
        Assert.False(plan.Systems.Single(system => system.SystemId == "ps3").Shown);
        Assert.Equal("Install an emulator for this system.",
            plan.Systems.Single(system => system.SystemId == "snes").Problem);
        // A system with several folders links each under its source's name, the disconnected one kept as is.
        Assert.Equal(
        [
            new EsDeLink(Path.Combine("ps2", "PS2 card"), @"E:\PS2"),
            new EsDeLink(Path.Combine("ps2", "PS2_home"), ""),
            new EsDeLink("gc", @"D:\Games\GC")
        ], plan.Links);

        var document = XDocument.Parse(EsDeFiles.SystemsXml(plan));
        Assert.Contains(EsDeFiles.Marker, document.Nodes().OfType<XComment>().Single().Value);
        Assert.Equal(["ps2", "gc"], document.Root!.Elements("system").Select(system => system.Element("name")!.Value));
        var cube = document.Root.Elements("system").Single(system => system.Element("name")!.Value == "gc");
        Assert.Equal(@"%ROMPATH%\gc", cube.Element("path")!.Value);
        Assert.Equal("Dolphin", cube.Element("command")!.Attribute("label")!.Value);
    }

    [Fact]
    public void LinksFollowThePlanWithoutEnteringTheirTargets()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Root, "es-de-roms");
        var first = Directory.CreateDirectory(Path.Combine(temporary.Root, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(temporary.Root, "second")).FullName;
        File.WriteAllText(Path.Combine(first, "game.iso"), "kept");
        File.WriteAllText(Path.Combine(second, "game.iso"), "kept");

        Assert.Empty(EsDeFiles.ReconcileLinks(root,
            [new EsDeLink(Path.Combine("ps2", "First"), first), new EsDeLink(Path.Combine("ps2", "Second"), second)],
            EmulatorPortableSetup.CreateAlias));
        Assert.True(File.Exists(Path.Combine(root, "ps2", "Second", "game.iso")));

        Assert.Empty(EsDeFiles.ReconcileLinks(root, [new EsDeLink("ps2", first)], EmulatorPortableSetup.CreateAlias));
        Assert.True(File.Exists(Path.Combine(root, "ps2", "game.iso")));
        Assert.NotNull(new DirectoryInfo(Path.Combine(root, "ps2")).LinkTarget);

        // A disconnected folder keeps its link; an unplanned one is unlinked, its files untouched.
        Assert.Empty(EsDeFiles.ReconcileLinks(root, [new EsDeLink("ps2", "")], EmulatorPortableSetup.CreateAlias));
        Assert.NotNull(new DirectoryInfo(Path.Combine(root, "ps2")).LinkTarget);
        Assert.Empty(EsDeFiles.ReconcileLinks(root, [], EmulatorPortableSetup.CreateAlias));
        Assert.False(Directory.Exists(Path.Combine(root, "ps2")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(first, "game.iso")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(second, "game.iso")));
    }

    [Fact]
    public void RomFolderSettingChangesOnlyItsEntryAndRespectsTheUsersChoice()
    {
        using var temporary = new TemporaryDirectory();
        var roms = Path.Combine(temporary.Root, "es-de-roms");
        Assert.Equal(EsDeRomFolderSetting.Set, EsDeFiles.SetRomDirectory(temporary.Root, roms, false));
        Assert.Equal(roms, EsDeFiles.ReadRomDirectory(temporary.Root));

        var settings = EsDeFiles.SettingsPath(temporary.Root);
        File.WriteAllText(settings, "<?xml version=\"1.0\"?>\n<bool name=\"ShowHelpPrompts\" value=\"true\" />\n"
                                    + "<string name=\"ROMDirectory\" value=\"%ESPATH%\\ROMs\" />\n");
        Assert.Equal(EsDeRomFolderSetting.Elsewhere, EsDeFiles.SetRomDirectory(temporary.Root, roms, false));
        Assert.Equal(EsDeRomFolderSetting.Set, EsDeFiles.SetRomDirectory(temporary.Root, roms, true));
        Assert.Equal(EsDeRomFolderSetting.Unchanged, EsDeFiles.SetRomDirectory(temporary.Root, roms, false));
        Assert.Contains("<bool name=\"ShowHelpPrompts\" value=\"true\" />", File.ReadAllText(settings));
        Assert.Equal(roms, EsDeFiles.ReadRomDirectory(temporary.Root));
    }

    [Fact]
    public void ReleaseFeedGivesThePortablePackageWithItsMd5OnEsDesHost()
    {
        const string feed = """
            {"stable":{"version":"3.5.0","packages":[
              {"name":"WindowsPortable","filename":"ES-DE_3.5.0-x64_Portable.zip",
               "url":"https://packages.es-de.org/release/3.5.0/ES-DE_3.5.0-x64_Portable.zip",
               "md5":"C13A725B973C1B6B2234A0C414DC4D4E"},
              {"name":"WindowsInstaller","filename":"ES-DE_3.5.0-x64.exe",
               "url":"https://packages.es-de.org/release/3.5.0/ES-DE_3.5.0-x64.exe","md5":"2193de4c8cf94538735339091fb18506"}]}}
            """;
        using var document = JsonDocument.Parse(feed);

        var release = EmulatorReleases.EsDeRelease(document.RootElement, "WindowsPortable");

        Assert.Equal("3.5.0", release.Version);
        Assert.Equal("ES-DE_3.5.0-x64_Portable.zip", release.Asset.Name);
        Assert.Equal("c13a725b973c1b6b2234a0c414dc4d4e", release.Asset.Md5);
        Assert.Equal("", release.Asset.Sha256);
        using var moved = JsonDocument.Parse(feed.Replace("packages.es-de.org", "example.com"));
        Assert.Throws<InvalidDataException>(() => EmulatorReleases.EsDeRelease(moved.RootElement, "WindowsPortable"));
    }

    private static EmulatorInstallation Standalone(string id, string name, string[] systems, string[] arguments)
    {
        return new EmulatorInstallation
        {
            Id = id, DefinitionId = id, Name = name, Managed = true, Channel = "stable", Systems = systems,
            ExecutablePath = Path.Combine(Program, id, id + ".exe"), DataPath = Path.Combine(Program, id + "-data"),
            LaunchArguments = arguments
        };
    }

    private static EmulatorInstallation RetroArch(EmulatorCore core)
    {
        return Standalone("retroarch", "RetroArch", [], ["-c", "{config}", "-L", "{core}", "-f", "{rom}"]) with
        {
            DataPolicy = new EmulatorDataPolicy { HasCores = true, ConfigFile = "retroarch.cfg" },
            Cores = [core]
        };
    }
}
