using System.Text.Json;
using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core;

public sealed class EmulatorPortableTests
{
    public static IEnumerable<object[]> Channels()
    {
        foreach (var definition in EmulatorCatalog.LoadBundled().Definitions)
        {
            foreach (var channel in definition.Sources.Keys)
            {
                yield return [definition.Id, channel];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public void EveryChannelUsesItsNativePortableBinding(string id, string channel)
    {
        using var temporary = new TemporaryDirectory();
        var installed = Installation(temporary.Root, id, "current") with { Channel = channel };
        var data = Path.Combine(temporary.Root, "EmulatorData", id);
        installed = EmulatorPortableSetup.Prepare(installed, data, null, CancellationToken.None, true);
        EmulatorManager.ConfigureData(installed);
        EmulatorPortable.Verify(installed);
        var arguments = installed.LaunchArguments.Select(value => value.Replace("{data}", installed.DataPath)
            .Replace("{config}", Path.Combine(installed.DataPath, installed.DataPolicy.ConfigFile))).ToArray();
        EmulatorPortable.VerifyArguments(installed, arguments);
        Assert.Equal(id, installed.Id);
        Assert.Equal("original-release", installed.ReleaseId);
        Assert.Equal(channel, installed.Channel);
        Assert.Empty(installed.DataPolicy.NativeRoots);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("repair")]
    public void DuckStationReplacementPreservesAllUserFilesAndAtomicSettingsWrites(string operation)
    {
        using var temporary = new TemporaryDirectory();
        var previous = Installation(temporary.Root, "duckstation", "old");
        var oldProgram = Path.GetDirectoryName(previous.ExecutablePath)!;
        previous = previous with { DataPath = oldProgram };
        File.WriteAllText(Path.Combine(oldProgram, "portable.txt"), "");
        var authored = new[]
        {
            "settings.ini", "memcards/card.mcd", "savestates/game.state", "inputprofiles/pad.ini",
            "cheats/game.cht", "covers/game.png", "textures/game/custom.png", "resources/overlays/user.txt",
            "custom-user-file.txt"
        };
        foreach (var file in authored)
        {
            Write(oldProgram, file, "user: " + file);
        }

        // DuckStation commits its INI by rename. The replacement must read the current file, not a stale hard link.
        AtomicFile.WriteText(Path.Combine(oldProgram, "settings.ini"), "user atomic settings", true);
        var candidate = Installation(temporary.Root, "duckstation", operation);
        var program = Path.GetDirectoryName(candidate.ExecutablePath)!;
        Write(program, "settings.ini", "package template");
        Write(program, "resources/new-resource.dat", "new package resource");
        var installed = EmulatorPortableSetup.Prepare(candidate,
            Path.Combine(temporary.Root, "EmulatorData", "duckstation"),
            previous, CancellationToken.None, true);
        EmulatorPortable.Verify(installed);
        Assert.Equal(program, installed.DataPath);
        Assert.Equal("user atomic settings", File.ReadAllText(Path.Combine(program, "settings.ini")));
        foreach (var file in authored.Where(file => file != "settings.ini"))
        {
            Assert.Equal("user: " + file, File.ReadAllText(Path.Combine(program, file)));
        }

        Assert.Contains(Directory.EnumerateFiles(program, "settings.ini.before-portable-*"),
            file => File.ReadAllText(file) == "package template");
        Assert.Equal("new package resource", File.ReadAllText(Path.Combine(program, "resources/new-resource.dat")));
        Assert.Equal("user atomic settings", File.ReadAllText(Path.Combine(oldProgram, "settings.ini")));
    }

    [Theory]
    [InlineData("duckstation", "memcards/card.mcd")]
    [InlineData("eden", "nand/save/game.bin")]
    public void LegacyNativeDataIsCopiedIntoPortableOwnershipAndTheOriginalRemains(string id, string file)
    {
        using var temporary = new TemporaryDirectory();
        var installed = Installation(temporary.Root, id, "current");
        var original = Directory.CreateDirectory(Path.Combine(temporary.Root, "old-native-user-data")).FullName;
        Write(original, file, "original user data");
        installed = installed with { DataPath = original, OwnsData = false };
        var migrated = EmulatorPortableSetup.Prepare(installed,
            Path.Combine(temporary.Root, "EmulatorData", installed.Id), installed, CancellationToken.None, false);
        EmulatorPortable.Verify(migrated);
        Assert.Equal(installed.Id, migrated.Id);
        Assert.Equal(installed.Version, migrated.Version);
        Assert.Equal("original user data", File.ReadAllText(Path.Combine(migrated.DataPath, file)));
        Assert.Equal("original user data", File.ReadAllText(Path.Combine(original, file)));
    }

    [Fact]
    public void RetroArchLaunchCannotOverrideTheReviewedConfigAfterItsPortableArgument()
    {
        using var temporary = new TemporaryDirectory();
        var installed = Installation(temporary.Root, "retroarch", "current");
        installed = EmulatorPortableSetup.Prepare(installed, installed.DataPath, null, CancellationToken.None, true);
        EmulatorManager.ConfigureData(installed);
        var config = Path.Combine(installed.DataPath, "retroarch.cfg");
        Assert.Throws<InvalidDataException>(() => EmulatorPortable.VerifyArguments(installed,
            ["-c", config, "--config=" + Path.Combine(temporary.Root, "outside.cfg")]));
        Assert.Equal(new[] { "-c", config, "-f", "game.rom" },
            EmulatorPortable.BindArguments(installed, ["-f", "game.rom"]));
        IniFile.SetValue(config, "savefiles_in_content_dir", "true");
        Assert.Throws<InvalidDataException>(() => EmulatorPortable.Verify(installed));
    }

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("savefiles_in_content_dir", "", 1)]
    [InlineData("systemfiles_in_content_dir", "", 1)]
    [InlineData("", "savestates_in_content_dir", 1)]
    [InlineData("", "screenshots_in_content_dir", 1)]
    public void RetroArchOnlyLoadsKnownRomPathsWhenContentDataNeedsPreservation(
        string mainFlag, string overrideFlag, int expectedCalls)
    {
        using var temporary = new TemporaryDirectory();
        var installed = Installation(temporary.Root, "retroarch", "current");
        var config = Write(installed.DataPath, "retroarch.cfg", "rgui_config_directory = \"config\"\n");
        var coreConfig = Write(installed.DataPath, "config/Core/game.cfg", "# per-game settings\n");
        if (mainFlag.Length > 0)
        {
            IniFile.SetValue(config, mainFlag, "true");
        }

        if (overrideFlag.Length > 0)
        {
            IniFile.SetValue(coreConfig, overrideFlag, "true");
        }

        var rom = Write(temporary.Root, "roms/game.rom", "content");
        var calls = 0;
        installed = EmulatorPortableSetup.Prepare(installed, installed.DataPath, null,
            CancellationToken.None, true, () =>
            {
                calls++;
                return new[] { rom };
            });
        Assert.Equal(expectedCalls, calls);
        if (mainFlag.Length > 0)
        {
            Assert.False(EmulatorPortable.IsTrue(IniFile.ReadValue(config, mainFlag)));
        }

        if (overrideFlag.Length > 0)
        {
            Assert.False(EmulatorPortable.IsTrue(IniFile.ReadValue(coreConfig, overrideFlag)));
        }

        EmulatorManager.ConfigureData(installed);
        EmulatorPortable.Verify(installed);
    }

    [Fact]
    public void RetroArchReplacementPreservesConfiguredDataAndPlaylistContentSidecars()
    {
        using var temporary = new TemporaryDirectory();
        var previous = Installation(temporary.Root, "retroarch", "old") with
        {
            DataPath = Path.Combine(temporary.Root, "legacy-retroarch-data")
        };
        var config = Write(previous.DataPath, "retroarch.cfg", "# original main settings\n");
        var nativeSaves = Path.Combine(temporary.Root, "native-main-saves");
        var nativeOverrides = Path.Combine(temporary.Root, "native-overrides");
        var nativeCoreSaves = Path.Combine(temporary.Root, "native-core-saves");
        var nativeCoreStates = Path.Combine(temporary.Root, "native-core-states");
        var nativeCoreImages = Path.Combine(temporary.Root, "native-core-images");
        var nativePlaylists = Path.Combine(temporary.Root, "native-playlists");
        var nativeHistory = Path.Combine(temporary.Root, "native-history/history.lpl");
        IniFile.SetValue(config, "savefile_directory", QuotedPath(nativeSaves));
        IniFile.SetValue(config, "rgui_config_directory", QuotedPath(nativeOverrides));
        IniFile.SetValue(config, "playlist_directory", QuotedPath(nativePlaylists));
        IniFile.SetValue(config, "content_history_path", QuotedPath(nativeHistory));
        IniFile.SetValue(config, "savefiles_in_content_dir", "true");
        IniFile.SetValue(config, "sort_savefiles_enable", "false");
        IniFile.SetValue(config, "sort_savestates_enable", "false");
        var coreConfig = Write(nativeOverrides, "Core/game.cfg", "# original game override\n");
        IniFile.SetValue(coreConfig, "savefile_directory", QuotedPath(nativeCoreSaves));
        IniFile.SetValue(coreConfig, "savestate_directory", QuotedPath(nativeCoreStates));
        IniFile.SetValue(coreConfig, "screenshot_directory", QuotedPath(nativeCoreImages));
        IniFile.SetValue(coreConfig, "savestates_in_content_dir", "true");
        IniFile.SetValue(coreConfig, "screenshots_in_content_dir", "true");
        IniFile.SetValue(coreConfig, "sort_savefiles_enable", "false");
        IniFile.SetValue(coreConfig, "sort_savestates_enable", "false");
        Write(nativeSaves, "configured.srm", "main configured save");
        Write(nativeCoreSaves, "configured.srm", "override configured save");
        Write(nativeCoreStates, "configured.state", "override configured state");
        Write(nativeCoreImages, "configured.png", "override configured image");
        var rom = Write(temporary.Root, "roms/game.rom", "content");
        Write(temporary.Root, "roms/game.srm", "content save");
        Write(temporary.Root, "roms/game.rtc", "content clock");
        Write(temporary.Root, "roms/game.state2", "numbered content state");
        Write(temporary.Root, "roms/game.state2.png", "state thumbnail");
        Write(temporary.Root, "roms/game.png", "content screenshot");
        Write(nativePlaylists, "Games.lpl", JsonSerializer.Serialize(new { items = new[] { new { path = rom } } }));
        var historyRom = Write(temporary.Root, "roms/history.rom", "history content");
        Write(temporary.Root, "roms/history.srm", "history content save");
        Write(Path.GetDirectoryName(nativeHistory)!, Path.GetFileName(nativeHistory),
            JsonSerializer.Serialize(new { items = new[] { new { path = historyRom } } }));
        var originalMain = File.ReadAllText(config);
        var originalOverride = File.ReadAllText(coreConfig);
        var installed = Installation(temporary.Root, "retroarch", "replacement");
        installed = EmulatorPortableSetup.Prepare(installed, installed.DataPath, previous,
            CancellationToken.None, true, () => Array.Empty<string>());
        EmulatorManager.ConfigureData(installed);
        EmulatorPortable.Verify(installed);
        Assert.Equal("main configured save",
            File.ReadAllText(Path.Combine(installed.DataPath, "saves/configured.srm")));
        Assert.Equal("content save", File.ReadAllText(Path.Combine(installed.DataPath, "saves/game.srm")));
        Assert.Equal("content clock", File.ReadAllText(Path.Combine(installed.DataPath, "saves/game.rtc")));
        Assert.Equal("history content save", File.ReadAllText(Path.Combine(installed.DataPath, "saves/history.srm")));
        var overrideData = Path.Combine(installed.DataPath, "override-data/Core/game");
        Assert.Equal("override configured save", File.ReadAllText(Path.Combine(overrideData, "saves/configured.srm")));
        Assert.Equal("content save", File.ReadAllText(Path.Combine(overrideData, "saves/game.srm")));
        Assert.Equal("override configured state",
            File.ReadAllText(Path.Combine(overrideData, "states/configured.state")));
        Assert.Equal("numbered content state", File.ReadAllText(Path.Combine(overrideData, "states/game.state2")));
        Assert.Equal("state thumbnail", File.ReadAllText(Path.Combine(overrideData, "states/game.state2.png")));
        Assert.Equal("override configured image",
            File.ReadAllText(Path.Combine(overrideData, "screenshots/configured.png")));
        Assert.Equal("content screenshot", File.ReadAllText(Path.Combine(overrideData, "screenshots/game.png")));
        Assert.True(File.Exists(Path.Combine(installed.DataPath, "playlists/Games.lpl")));
        Assert.True(File.Exists(Path.Combine(installed.DataPath, "content_history.lpl")));
        Assert.Equal(originalMain, File.ReadAllText(config));
        Assert.Equal(originalOverride, File.ReadAllText(coreConfig));
        Assert.Equal("content save", File.ReadAllText(Path.ChangeExtension(rom, ".srm")));
        Assert.Equal("main configured save", File.ReadAllText(Path.Combine(nativeSaves, "configured.srm")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetroArchReplacementPreservesContentAndCoreSortedSidecars(bool sortByContent)
    {
        using var temporary = new TemporaryDirectory();
        var previous = Installation(temporary.Root, "retroarch", "old") with
        {
            DataPath = Path.Combine(temporary.Root, "legacy-retroarch-data")
        };
        var config = Write(previous.DataPath, "retroarch.cfg",
            "savefiles_in_content_dir = true\nsavestates_in_content_dir = true\n");
        var sorting = sortByContent ? "true" : "false";
        IniFile.SetValue(config, "sort_savefiles_enable", "true");
        IniFile.SetValue(config, "sort_savestates_enable", "true");
        IniFile.SetValue(config, "sort_savefiles_by_content_enable", sorting);
        IniFile.SetValue(config, "sort_savestates_by_content_enable", sorting);
        var nativeSaves = Path.Combine(temporary.Root, "native-main-saves");
        IniFile.SetValue(config, "savefile_directory", QuotedPath(nativeSaves));
        var romParent = Path.Combine(temporary.Root, "roms", "Collection");
        var rom = Write(romParent, "game.rom", "content");
        var suffix = sortByContent ? Path.Combine("Collection", "CoreLibrary") : "CoreLibrary";
        var sidecars = Path.Combine(romParent, suffix);
        Write(sidecars, "game.srm", "sorted content save");
        Write(sidecars, "game.rtc", "sorted content clock");
        Write(sidecars, "game.state2", "sorted content state");
        Write(sidecars, "game.state2.png", "sorted state thumbnail");
        Write(sidecars, "unrelated.txt", "unrelated core-directory content");
        Write(nativeSaves, Path.Combine(suffix, "configured.srm"), "existing configured sorted save");
        var originalConfig = File.ReadAllText(config);
        var installed = Installation(temporary.Root, "retroarch", "replacement") with
        {
            Cores = [new EmulatorCore { Id = "test_core", Name = "Different Display Name" }]
        };
        installed = EmulatorPortableSetup.Prepare(installed, installed.DataPath, previous,
            CancellationToken.None, true, () => new[] { rom });
        EmulatorManager.ConfigureData(installed);
        EmulatorPortable.Verify(installed);
        var portableSaves = Path.Combine(installed.DataPath, "saves", suffix);
        var portableStates = Path.Combine(installed.DataPath, "states", suffix);
        Assert.Equal("sorted content save", File.ReadAllText(Path.Combine(portableSaves, "game.srm")));
        Assert.Equal("sorted content clock", File.ReadAllText(Path.Combine(portableSaves, "game.rtc")));
        Assert.Equal("sorted content state", File.ReadAllText(Path.Combine(portableStates, "game.state2")));
        Assert.Equal("sorted state thumbnail", File.ReadAllText(Path.Combine(portableStates, "game.state2.png")));
        Assert.Equal("existing configured sorted save",
            File.ReadAllText(Path.Combine(portableSaves, "configured.srm")));
        Assert.False(File.Exists(Path.Combine(portableSaves, "unrelated.txt")));
        var portableConfig = Path.Combine(installed.DataPath, "retroarch.cfg");
        Assert.Equal("true", IniFile.ReadValue(portableConfig, "sort_savefiles_enable"));
        Assert.Equal("true", IniFile.ReadValue(portableConfig, "sort_savestates_enable"));
        Assert.Equal(sorting, IniFile.ReadValue(portableConfig, "sort_savefiles_by_content_enable"));
        Assert.Equal(sorting, IniFile.ReadValue(portableConfig, "sort_savestates_by_content_enable"));
        Assert.Equal(originalConfig, File.ReadAllText(config));
        Assert.Equal("sorted content save", File.ReadAllText(Path.Combine(sidecars, "game.srm")));
        Assert.Equal("sorted content state", File.ReadAllText(Path.Combine(sidecars, "game.state2")));
        Assert.Equal("existing configured sorted save",
            File.ReadAllText(Path.Combine(nativeSaves, suffix, "configured.srm")));
    }

    [Fact]
    public void RetroArchCoreSortedFlatSidecarFailsBeforeCopyingOrChangingFlags()
    {
        using var temporary = new TemporaryDirectory();
        var previous = Installation(temporary.Root, "retroarch", "old") with
        {
            DataPath = Path.Combine(temporary.Root, "legacy-retroarch-data")
        };
        var config = Write(previous.DataPath, "retroarch.cfg",
            "savefiles_in_content_dir = true\nsort_savefiles_enable = true\n");
        var rom = Write(temporary.Root, "roms/game.rom", "content");
        var save = Write(temporary.Root, "roms/game.srm", "ambiguous flat save");
        Write(previous.DataPath, "saves/retained.srm", "retained configured save");
        var history = Write(previous.DataPath, "content_history.lpl", JsonSerializer.Serialize(new
        {
            items = new[] { new { path = rom, core_name = "Misleading Display Name" } }
        }));
        IniFile.SetValue(config, "content_history_path", QuotedPath(history));
        var originalConfig = File.ReadAllText(config);
        var installed = Installation(temporary.Root, "retroarch", "replacement") with
        {
            Cores = [new EmulatorCore { Id = "test_core", Name = "Misleading Display Name" }]
        };
        Assert.Throws<InvalidDataException>(() => EmulatorPortableSetup.Prepare(installed,
            installed.DataPath, previous, CancellationToken.None, true, () => new[] { rom }));
        Assert.False(Directory.Exists(installed.DataPath));
        Assert.Equal(originalConfig, File.ReadAllText(config));
        Assert.Equal("ambiguous flat save", File.ReadAllText(save));
        Assert.Equal("retained configured save",
            File.ReadAllText(Path.Combine(previous.DataPath, "saves/retained.srm")));
    }

    [Fact]
    public void RetroArchGameOverrideKeepsInheritedNativeCoreSorting()
    {
        using var temporary = new TemporaryDirectory();
        var installed = Installation(temporary.Root, "retroarch", "current");
        Write(installed.DataPath, "retroarch.cfg", "rgui_config_directory = \"config\"\n"
                                                   + "savefiles_in_content_dir = true\nsort_savefiles_enable = false\nsort_savestates_enable = false\n");
        var core = Write(installed.DataPath, "config/CoreLibrary/CoreLibrary.cfg", "sort_savefiles_enable = true\n");
        var game = Write(installed.DataPath, "config/CoreLibrary/game.cfg", "# inherits native core sorting\n");
        var rom = Write(temporary.Root, "roms/game.rom", "content");
        var save = Write(temporary.Root, "roms/CoreLibrary/game.srm", "inherited sorted save");
        installed = EmulatorPortableSetup.Prepare(installed, installed.DataPath, null,
            CancellationToken.None, true, () => new[] { rom });
        EmulatorManager.ConfigureData(installed);
        EmulatorPortable.Verify(installed);
        var saves = IniFile.ReadValue(game, "savefile_directory")!.Trim('"');
        Assert.Equal("inherited sorted save", File.ReadAllText(Path.Combine(saves, "CoreLibrary/game.srm")));
        Assert.Equal("true", IniFile.ReadValue(core, "sort_savefiles_enable"));
        Assert.Null(IniFile.ReadValue(game, "sort_savefiles_enable"));
        Assert.Equal("inherited sorted save", File.ReadAllText(save));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetroArchMissingRomMediaFailsBeforeDisablingMainOrOverrideFlags(bool missingDirectory)
    {
        using var temporary = new TemporaryDirectory();
        var previous = Installation(temporary.Root, "retroarch", "old") with
        {
            DataPath = Path.Combine(temporary.Root, "legacy-retroarch-data")
        };
        var config = Write(previous.DataPath, "retroarch.cfg",
            "rgui_config_directory = \"config\"\nsavefiles_in_content_dir = true\n");
        var coreConfig = Write(previous.DataPath, "config/Core/game.cfg", "savestates_in_content_dir = true\n");
        Write(previous.DataPath, "saves/retained.srm", "retained save");
        var originalMain = File.ReadAllText(config);
        var originalOverride = File.ReadAllText(coreConfig);
        var media = Path.Combine(temporary.Root, "unavailable-media");
        if (!missingDirectory)
        {
            Directory.CreateDirectory(media);
        }

        var installed = Installation(temporary.Root, "retroarch", "replacement");
        Assert.Throws<InvalidDataException>(() => EmulatorPortableSetup.Prepare(installed,
            installed.DataPath, previous, CancellationToken.None, true,
            () => new[] { Path.Combine(media, "game.rom") }));
        Assert.Equal(originalMain, File.ReadAllText(config));
        Assert.Equal(originalOverride, File.ReadAllText(coreConfig));
        Assert.False(Directory.Exists(installed.DataPath));
        Assert.Equal("retained save", File.ReadAllText(Path.Combine(previous.DataPath, "saves/retained.srm")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetroArchContentFirmwareCopiesOnlyDeclaredFiles(bool absoluteRequirement)
    {
        using var temporary = new TemporaryDirectory();
        var installed = Installation(temporary.Root, "retroarch", "current");
        installed = installed with
        {
            Cores =
            [
                new EmulatorCore
                {
                    Id = "test-core",
                    RequiredFiles =
                    [
                        absoluteRequirement
                            ? Path.Combine(installed.DataPath, "system/firmware/console.bin")
                            : "firmware/console.bin"
                    ]
                }
            ]
        };
        var config = Write(installed.DataPath, "retroarch.cfg", "systemfiles_in_content_dir = true\n");
        var rom = Write(temporary.Root, "roms/game.rom", "content");
        Write(temporary.Root, "roms/firmware/console.bin", "declared firmware");
        Write(temporary.Root, "roms/unrelated.bin", "unrelated content");
        Write(temporary.Root, "roms/other/private.dat", "unrelated nested content");
        installed = EmulatorPortableSetup.Prepare(installed, installed.DataPath, null,
            CancellationToken.None, true, () => new[] { rom });
        Assert.Equal("declared firmware",
            File.ReadAllText(Path.Combine(installed.DataPath, "system/firmware/console.bin")));
        Assert.False(File.Exists(Path.Combine(installed.DataPath, "system/game.rom")));
        Assert.False(File.Exists(Path.Combine(installed.DataPath, "system/unrelated.bin")));
        Assert.False(Directory.Exists(Path.Combine(installed.DataPath, "system/other")));
        Assert.False(EmulatorPortable.IsTrue(IniFile.ReadValue(config, "systemfiles_in_content_dir")));
        Assert.Equal("declared firmware", File.ReadAllText(Path.Combine(temporary.Root, "roms/firmware/console.bin")));
        EmulatorManager.ConfigureData(installed);
        EmulatorPortable.Verify(installed);
    }

    [Fact]
    public void RetroArchRelativeLaunchPathsUseTheExecutableWorkingDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var installed = Installation(temporary.Root, "retroarch", "current");
        installed = EmulatorPortableSetup.Prepare(installed, installed.DataPath, null, CancellationToken.None, true);
        EmulatorManager.ConfigureData(installed);
        var program = Path.GetDirectoryName(installed.ExecutablePath)!;
        var config = Path.Combine(installed.DataPath, "retroarch.cfg");
        var appended = Write(installed.DataPath, "config/Core/game.cfg", "savefiles_in_content_dir = false\n");
        EmulatorPortable.VerifyArguments(installed,
        [
            "-c", Path.GetRelativePath(program, config),
            "-s", Path.GetRelativePath(program, Path.Combine(installed.DataPath, "saves/game.srm")),
            "-S", Path.GetRelativePath(program, Path.Combine(installed.DataPath, "states/game.state")),
            "--appendconfig", Path.GetRelativePath(program, appended)
        ]);
        Assert.Throws<InvalidDataException>(() => EmulatorPortable.VerifyArguments(installed,
            ["-c", Path.GetRelativePath(program, config), "-s", "saves/game.srm"]));
    }

    [Fact]
    public void DolphinRelativeUserArgumentUsesTheExecutableWorkingDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var installed = Installation(temporary.Root, "dolphin", "current");
        installed = EmulatorPortableSetup.Prepare(installed, installed.DataPath, null, CancellationToken.None, true);
        var program = Path.GetDirectoryName(installed.ExecutablePath)!;
        EmulatorPortable.VerifyArguments(installed, ["-u", Path.GetRelativePath(program, installed.DataPath)]);
    }

    [Fact]
    public void RetroArchConfiguredAndLaunchSaveDescendantsCannotEscapeThroughALink()
    {
        using var temporary = new TemporaryDirectory();
        var installed = Installation(temporary.Root, "retroarch", "current");
        installed = EmulatorPortableSetup.Prepare(installed, installed.DataPath, null, CancellationToken.None, true);
        EmulatorManager.ConfigureData(installed);
        var external = Directory.CreateDirectory(Path.Combine(temporary.Root, "external-user-data")).FullName;
        Write(external, "retained.srm", "external save");
        var link = Path.Combine(installed.DataPath, "saves", "linked-card");
        EmulatorPortableSetup.CreateAlias(link, external);
        try
        {
            var config = Path.Combine(installed.DataPath, "retroarch.cfg");
            IniFile.SetValue(config, "savefile_directory", "\"saves/linked-card/future-folder\"");
            Assert.Throws<InvalidDataException>(() => EmulatorPortable.Verify(installed));
            Assert.Throws<InvalidDataException>(() => EmulatorPortable.VerifyArguments(installed,
                ["-c", config, "-s", Path.Combine(link, "future-folder/game.srm")]));
            Assert.Equal("external save", File.ReadAllText(Path.Combine(external, "retained.srm")));
            Assert.False(Directory.Exists(Path.Combine(external, "future-folder")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void ManagedRepairMaterializesLinkedUserDataWithoutChangingItsTarget()
    {
        using var temporary = new TemporaryDirectory();
        var installed = Installation(temporary.Root, "eden", "current");
        var original = Path.Combine(temporary.Root, "original-user-data");
        Write(original, "save/game.bin", "original save");
        Directory.CreateDirectory(installed.DataPath);
        var linked = Path.Combine(installed.DataPath, "nand");
        EmulatorPortableSetup.CreateAlias(linked, original);
        installed = EmulatorPortableSetup.Prepare(installed, installed.DataPath, null, CancellationToken.None, true);
        EmulatorPortable.Verify(installed);
        Assert.Null(new DirectoryInfo(linked).LinkTarget);
        Assert.Equal("original save", File.ReadAllText(Path.Combine(linked, "save/game.bin")));
        Write(linked, "save/game.bin", "owned updated save");
        Assert.Equal("original save", File.ReadAllText(Path.Combine(original, "save/game.bin")));
    }

    [Fact]
    public void ManagedRepairRefusesRecursiveUserDataLinksWithoutDeletingTheOriginal()
    {
        using var temporary = new TemporaryDirectory();
        var installed = Installation(temporary.Root, "eden", "current");
        Write(installed.DataPath, "save/game.bin", "original save");
        var loop = Path.Combine(installed.DataPath, "loop");
        EmulatorPortableSetup.CreateAlias(loop, installed.DataPath);
        try
        {
            Assert.Throws<InvalidDataException>(() => EmulatorPortableSetup.Prepare(installed,
                installed.DataPath, null, CancellationToken.None, true));
            Assert.Equal("original save", File.ReadAllText(Path.Combine(installed.DataPath, "save/game.bin")));
            Assert.NotNull(new DirectoryInfo(loop).LinkTarget);
        }
        finally
        {
            Directory.Delete(loop);
        }
    }

    [Fact]
    public void NativePortableAliasPreservesAnExistingExternalUserDirectoryBeforeRebinding()
    {
        using var temporary = new TemporaryDirectory();
        var installed = Installation(temporary.Root, "eden", "current");
        var original = Path.Combine(temporary.Root, "existing-native-user-data");
        Write(original, "nand/save/game.bin", "native user save");
        var alias = Path.Combine(Path.GetDirectoryName(installed.ExecutablePath)!, "user");
        EmulatorPortableSetup.CreateAlias(alias, original);
        installed = EmulatorPortableSetup.Prepare(installed, installed.DataPath, null, CancellationToken.None, true);
        EmulatorPortable.Verify(installed);
        Assert.Equal("native user save", File.ReadAllText(Path.Combine(installed.DataPath, "nand/save/game.bin")));
        Assert.Equal("native user save", File.ReadAllText(Path.Combine(original, "nand/save/game.bin")));
        Assert.Equal(EmulatorPortable.PhysicalPath(installed.DataPath), EmulatorPortable.PhysicalPath(alias));
    }

    [Fact]
    public void RetiredVersionCopiesRealLinkedUserDataAndExcludesOnlyItsRetainedNativeAlias()
    {
        using var temporary = new TemporaryDirectory();
        var installed = Installation(temporary.Root, "eden", "current");
        installed = EmulatorPortableSetup.Prepare(installed, installed.DataPath, null, CancellationToken.None, true);
        var program = Path.GetDirectoryName(installed.ExecutablePath)!;
        var original = Path.Combine(temporary.Root, "external-custom-saves");
        Write(original, "game.bin", "custom linked save");
        Write(installed.DataPath, "nand/retained.bin", "already retained save");
        EmulatorPortableSetup.CreateAlias(Path.Combine(program, "custom-saves"), original);
        EmulatorPortableSetup.PreserveVersion(installed, program, installed.DataPath, CancellationToken.None);
        var snapshot = Path.Combine(installed.DataPath, ".portable-snapshots", "current");
        Assert.False(Directory.Exists(Path.Combine(snapshot, "user")));
        Assert.Null(new DirectoryInfo(Path.Combine(snapshot, "custom-saves")).LinkTarget);
        Assert.Equal("custom linked save", File.ReadAllText(Path.Combine(snapshot, "custom-saves/game.bin")));
        Assert.Equal("custom linked save", File.ReadAllText(Path.Combine(original, "game.bin")));
        Assert.Equal("already retained save", File.ReadAllText(Path.Combine(installed.DataPath, "nand/retained.bin")));
    }

    [Fact]
    public void DolphinReplacementPreservesConfiguredSdWfsAndGbaData()
    {
        using var temporary = new TemporaryDirectory();
        var previous = Installation(temporary.Root, "dolphin", "old") with
        {
            DataPath = Path.Combine(temporary.Root, "legacy-dolphin-data")
        };
        var config = Write(previous.DataPath, "Config/Dolphin.ini", "# original Dolphin settings\n");
        var sd = Write(temporary.Root, "native-sd/card.raw", "SD card image");
        var sync = Path.Combine(temporary.Root, "native-sd/sync");
        var wfs = Path.Combine(temporary.Root, "native-wfs");
        var gba = Path.Combine(temporary.Root, "native-gba-saves");
        Write(sync, "sd-save.dat", "SD synchronization data");
        Write(wfs, "wfs-save.dat", "WFS data");
        Write(gba, "game.sav", "GBA configured save");
        IniFile.SetValue(config, "WiiSDCardPath", QuotedPath(sd), "General");
        IniFile.SetValue(config, "WiiSDCardSyncFolder", QuotedPath(sync), "General");
        IniFile.SetValue(config, "WFSPath", QuotedPath(wfs), "General");
        IniFile.SetValue(config, "SavesPath", QuotedPath(gba), "GBA");
        var originalConfig = File.ReadAllText(config);
        var installed = Installation(temporary.Root, "dolphin", "replacement");
        installed = EmulatorPortableSetup.Prepare(installed, installed.DataPath, previous, CancellationToken.None,
            true);
        EmulatorPortable.Verify(installed);
        Assert.Equal("SD card image", File.ReadAllText(Path.Combine(installed.DataPath, "Wii/card.raw")));
        Assert.Equal("SD synchronization data",
            File.ReadAllText(Path.Combine(installed.DataPath, "Wii/sdcard/sd-save.dat")));
        Assert.Equal("WFS data", File.ReadAllText(Path.Combine(installed.DataPath, "WFS/wfs-save.dat")));
        Assert.Equal("GBA configured save", File.ReadAllText(Path.Combine(installed.DataPath, "GBA/Saves/game.sav")));
        Assert.Equal(originalConfig, File.ReadAllText(config));
        Assert.Equal("SD card image", File.ReadAllText(sd));
        Assert.Equal("GBA configured save", File.ReadAllText(Path.Combine(gba, "game.sav")));
    }

    [Fact]
    public void DolphinAdmissionRejectsSdAndGbaSaveLaunchEscapes()
    {
        using var temporary = new TemporaryDirectory();
        var installed = Installation(temporary.Root, "dolphin", "current");
        installed = EmulatorPortableSetup.Prepare(installed, installed.DataPath, null, CancellationToken.None, true);
        EmulatorPortable.VerifyArguments(installed,
        [
            "-u", installed.DataPath, "-C",
            "Dolphin.General.WiiSDCardPath=" + Path.Combine(installed.DataPath, "Wii/card.raw")
        ]);
        Assert.Throws<InvalidDataException>(() => EmulatorPortable.VerifyArguments(installed,
        [
            "-u", installed.DataPath, "-C",
            "Dolphin.General.WiiSDCardPath=" + Path.Combine(temporary.Root, "outside.raw")
        ]));
        Assert.Throws<InvalidDataException>(() => EmulatorPortable.VerifyArguments(installed,
            ["-u", installed.DataPath, "-C", "Dolphin.GBA.SavesInRomPath=True"]));
        var config = Write(installed.DataPath, "Config/Dolphin.ini", "[GBA]\nSavesInRomPath = True\n");
        Assert.Throws<InvalidDataException>(() => EmulatorPortable.Verify(installed));
        Assert.Equal("True", IniFile.ReadValue(config, "SavesInRomPath", "GBA"));
    }

    [Fact]
    public void DolphinReplacementPreservesEveryGbaSlotAndGameBoyPlayerSaveName()
    {
        using var temporary = new TemporaryDirectory();
        var previous = Installation(temporary.Root, "dolphin", "old") with
        {
            DataPath = Path.Combine(temporary.Root, "legacy-dolphin-data")
        };
        var config = Write(previous.DataPath, "Config/Dolphin.ini", "[GBA]\nSavesInRomPath = True\n");
        var oldProgram = Path.GetDirectoryName(previous.ExecutablePath)!;
        for (var slot = 1; slot <= 5; slot++)
        {
            var rom = Write(oldProgram, "gba/cartridge" + slot + ".gba", "content");
            IniFile.SetValue(config, slot == 5 ? "GBPlayerRom" : "Rom" + slot,
                QuotedPath(Path.GetRelativePath(oldProgram, rom)), "GBA");
            Write(oldProgram, "gba/cartridge" + slot + "-" + slot + ".sav", "slot " + slot + " save");
        }

        var originalConfig = File.ReadAllText(config);
        var installed = Installation(temporary.Root, "dolphin", "replacement");
        installed = EmulatorPortableSetup.Prepare(installed, installed.DataPath, previous, CancellationToken.None,
            true);
        EmulatorPortable.Verify(installed);
        for (var slot = 1; slot <= 5; slot++)
        {
            var name = "cartridge" + slot + "-" + slot + ".sav";
            Assert.Equal("slot " + slot + " save",
                File.ReadAllText(Path.Combine(installed.DataPath, "GBA/Saves", name)));
            Assert.Equal("slot " + slot + " save", File.ReadAllText(Path.Combine(oldProgram, "gba", name)));
        }

        Assert.False(EmulatorPortable.IsTrue(IniFile.ReadValue(
            Path.Combine(installed.DataPath, "Config/Dolphin.ini"), "SavesInRomPath", "GBA")));
        Assert.Equal(originalConfig, File.ReadAllText(config));
    }

    [Fact]
    public async Task PruneAndRemovePreserveDuckStationDataBeforeDeletingAnyVersion()
    {
        using var temporary = new TemporaryDirectory();
        using var manager = new EmulatorManager(new UserDataContext(temporary.Root, "unused-test-config"));
        await manager.Initialization;
        var installed = Installation(temporary.Root, "duckstation", "current");
        installed = installed with { DataPath = Path.GetDirectoryName(installed.ExecutablePath)! };
        Write(installed.DataPath, "portable.txt", "");
        Write(installed.DataPath, "memcards/current.mcd", "current card");
        var retired = Installation(temporary.Root, "duckstation", "retired");
        Write(Path.GetDirectoryName(retired.ExecutablePath)!, "savestates/old.state", "retired state");
        File.WriteAllText(EmulatorStorage.StorePath(temporary.Root), JsonSerializer.Serialize(
            new EmulatorStore { Installations = [installed] }, EmulatorStorage.JsonOptions));
        manager.PruneVersions(installed, CancellationToken.None);
        var data = Path.Combine(temporary.Root, "EmulatorData", "duckstation");
        Assert.False(Directory.Exists(Path.GetDirectoryName(retired.ExecutablePath)));
        Assert.Equal("retired state",
            File.ReadAllText(Path.Combine(data, ".portable-snapshots", "retired", "savestates/old.state")));
        await manager.RemoveAsync(installed.Id, CancellationToken.None);
        Assert.False(Directory.Exists(installed.Root));
        Assert.Equal("current card", File.ReadAllText(Path.Combine(data, "memcards/current.mcd")));
        Assert.Equal("current card",
            File.ReadAllText(Path.Combine(data, ".portable-snapshots", "current", "memcards/current.mcd")));
        Assert.Empty(EmulatorStorage.ReadInstallations(temporary.Root));
    }

    [Theory]
    [InlineData("eden", "user")]
    [InlineData("rpcs3", "portable")]
    [InlineData("dolphin", "User")]
    public void ProgramCleanupDoesNotFollowPortableDirectoryLinks(string id, string alias)
    {
        using var temporary = new TemporaryDirectory();
        var installed = Installation(temporary.Root, id, "current");
        var data = Path.Combine(temporary.Root, "EmulatorData", id);
        installed = EmulatorPortableSetup.Prepare(installed, data, null, CancellationToken.None, true);
        Write(data, "retained/save.bin", "retained save");
        var program = Path.GetDirectoryName(installed.ExecutablePath)!;
        Assert.NotNull(new DirectoryInfo(Path.Combine(program, alias)).LinkTarget);
        EmulatorPackages.DeleteOwned(Path.GetDirectoryName(program)!, installed.Root);
        Assert.Equal("retained save", File.ReadAllText(Path.Combine(data, "retained/save.bin")));
    }

    [Fact]
    public void FreshLaunchRefusesAChangedMarkerEvenWithAnUnchangedReceipt()
    {
        using var temporary = new TemporaryDirectory();
        var installed = Installation(temporary.Root, "duckstation", "current");
        installed = EmulatorPortableSetup.Prepare(installed, Path.Combine(temporary.Root, "EmulatorData", installed.Id),
                null, CancellationToken.None, true) with
            {
                DataPolicy = new EmulatorDataPolicy()
            };
        var store = new EmulatorStore { Installations = [installed] };
        EmulatorStorage.Resolve(store, installed.Id, "psx", null, "game.cue");
        File.Delete(Path.Combine(installed.DataPath, "portable.txt"));
        Assert.Throws<InvalidDataException>(() =>
            EmulatorStorage.Resolve(store, installed.Id, "psx", null, "game.cue"));
    }

    [Theory]
    [InlineData("duckstation")]
    [InlineData("eden")]
    [InlineData("rpcs3")]
    [InlineData("dolphin")]
    [InlineData("pcsx2")]
    [InlineData("retroarch")]
    public void ExternalNonPortableInstallIsRefusedWithoutChangingUserFiles(string id)
    {
        using var temporary = new TemporaryDirectory();
        Write(temporary.Root, "external-user-file.txt", "original");
        var failure = Assert.Throws<InvalidDataException>(() => EmulatorPortable.ReadExternalData(id, temporary.Root));
        Assert.Contains("portable", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("original", File.ReadAllText(Path.Combine(temporary.Root, "external-user-file.txt")));
        Assert.Single(Directory.EnumerateFiles(temporary.Root));
    }

    private static EmulatorInstallation Installation(string userRoot, string id, string version)
    {
        var definition = EmulatorCatalog.LoadBundled().Definitions.Single(item => item.Id == id);
        var root = Path.Combine(userRoot, "Emulators", id);
        var program = Directory.CreateDirectory(Path.Combine(root, "versions", version)).FullName;
        var executable = Path.Combine(program, "portable-fixture-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(executable, "fixture");
        return new EmulatorInstallation
        {
            Id = id, DefinitionId = id, Name = definition.Name, Managed = true, OwnsData = true,
            Root = root, ExecutablePath = executable, DataPath = Path.Combine(userRoot, "EmulatorData", id),
            Systems = definition.Systems, Version = version, ReleaseId = "original-release",
            DataPolicy = definition.DataPolicy,
            LaunchArguments = [.. definition.DataPolicy.DataArguments, .. definition.LaunchArguments]
        };
    }

    private static string Write(string root, string relative, string contents)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private static string QuotedPath(string path)
    {
        return '"' + path.Replace('\\', '/') + '"';
    }
}
