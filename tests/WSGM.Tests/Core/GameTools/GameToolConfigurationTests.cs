using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core;

public sealed class GameToolConfigurationTests
{
    [Theory]
    [InlineData("dxvk", "dxgiFps", "45")]
    [InlineData("reshade", "overlayKey", "113,1,0,0")]
    [InlineData("special-k", "fps", "45")]
    [InlineData("dgvoodoo2", "outputApi", "d3d12_fl11_0")]
    public async Task NativeConfigEditsPreserveUnknownKeysAndRestoreTheOriginal(string id, string settingId, string value)
    {
        using var directory = new TemporaryDirectory();
        var tool = GameToolCatalog.Current.Find(id)!.ForProxy(GameToolCatalog.Current.Find(id)!.Proxy.Default);
        var game = Path.Combine(directory.Root, "game");
        Directory.CreateDirectory(game);
        var original = id == "dxvk" ? "# custom\nuser.unknown = 17\n" : "[User]\nUnknown = 17 ; keep this\n";
        if (id == "dgvoodoo2")
        {
            original = "Version=0x287\n" + original;
        }
        var config = Path.Combine(game, tool.PrimaryConfig);
        File.WriteAllText(config, original);
        var target = new GameToolTarget(218, game, Path.Combine(game, "game.exe"), "x64", "fixture");
        var installer = new GameToolInstaller(Path.Combine(directory.Root, "data"));
        var package = Package(directory.Root, tool, "one", "x64");
        var installed = await installer.InstallAsync(tool, target, package,
            new GameToolRequest("install", tool.Proxy.Default, new Dictionary<string, string> { [settingId] = value }),
            null, () => null, _ => { }, CancellationToken.None);
        Assert.Contains(id == "dxvk" ? "user.unknown = 17" : "Unknown = 17 ; keep this", File.ReadAllText(config));
        Assert.Contains("17", File.ReadAllText(config));
        var field = tool.Settings.First(setting => setting.Id == settingId);
        Assert.Equal(field.Raw(value), GameToolIni.Read(config, [(field.Section, field.Key)])[GameToolIni.Id(field.Section, field.Key)]);
        if (id == "dxvk")
        {
            Assert.DoesNotContain("[", File.ReadAllText(config));
        }
        var configured = installer.Configure(tool, target, installed.Manifest!, null,
            new Dictionary<string, string> { [settingId] = value }, () => null);
        Assert.Contains("next game start", configured.Notice);
        Assert.Equal(1, configured.Manifest!.Replaced.Count(file => file.RelativePath == tool.PrimaryConfig));
        installer.Remove(tool, target, configured.Manifest);
        Assert.Equal(original, File.ReadAllText(config));
    }

    [Theory]
    [InlineData(false, null, null)]
    [InlineData(true, null, "true")]
    [InlineData(true, "off", "false")]
    public async Task SteamSilentUsesShortcutMetadataAndHonorsTheUser(bool shortcut, string? requested, string? expected)
    {
        using var directory = new TemporaryDirectory();
        var tool = GameToolCatalog.Current.Find("special-k")!.ForGame(shortcut).ForProxy("dxgi.dll");
        var game = Path.Combine(directory.Root, "game");
        Directory.CreateDirectory(game);
        var settings = new Dictionary<string, string>();
        if (requested is not null)
        {
            settings["steamSilent"] = requested;
        }
        var installer = new GameToolInstaller(Path.Combine(directory.Root, "data"));
        await installer.InstallAsync(tool, new GameToolTarget(218, game, Path.Combine(game, "game.exe"), "x64", "fixture"),
            Package(directory.Root, tool, "silent", "x64"), new GameToolRequest("install", "dxgi.dll", settings), null,
            () => null, _ => { }, CancellationToken.None);
        var raw = GameToolIni.Read(Path.Combine(game, "dxgi.ini"), [("Steam.Log", "Silent")]);
        Assert.Equal(expected, raw.GetValueOrDefault("steam.log/silent"));
        Assert.Equal(new byte[] { 0xff, 0xfe }, File.ReadAllBytes(Path.Combine(game, "dxgi.ini"))[..2]);
    }

    [Fact]
    public async Task ExistingShortcutSilentValueIsNotOverwrittenByTheDefault()
    {
        using var directory = new TemporaryDirectory();
        var tool = GameToolCatalog.Current.Find("special-k")!.ForGame(shortcut: true).ForProxy("d3d11.dll");
        var game = Path.Combine(directory.Root, "game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "d3d11.ini"), "[Steam.Log]\nSilent=false\n[Custom]\nKeep=yes\n");
        var installed = await new GameToolInstaller(directory.Root).InstallAsync(tool,
            new GameToolTarget(218, game, Path.Combine(game, "game.exe"), "x64", "fixture"),
            Package(directory.Root, tool, "existing", "x64"), new GameToolRequest("install", "d3d11.dll", new Dictionary<string, string>()),
            null, () => null, _ => { }, CancellationToken.None);
        Assert.Equal("off", installed.Manifest!.Settings["steamSilent"]);
        Assert.Contains("Silent=false", File.ReadAllText(Path.Combine(game, "d3d11.ini")));
        Assert.Contains("Keep=yes", File.ReadAllText(Path.Combine(game, "d3d11.ini")));
    }

    [Fact]
    public void NativeRangesAndHotkeysRejectInvalidInput()
    {
        var dxvk = GameToolCatalog.Current.Find("dxvk")!;
        Assert.NotNull(GameToolAdmission.Settings(dxvk, new Dictionary<string, string> { ["dxgiLatency"] = "17" }));
        Assert.NotNull(GameToolAdmission.Settings(dxvk, new Dictionary<string, string> { ["dxgiFps"] = "NaN" }));
        Assert.NotNull(GameToolAdmission.Settings(dxvk, new Dictionary<string, string> { ["hud"] = "fps" }));
        var reshade = GameToolCatalog.Current.Find("reshade")!;
        Assert.NotNull(GameToolAdmission.Settings(reshade, new Dictionary<string, string> { ["overlayKey"] = "256,0,0,0" }));
        Assert.Equal("36,1,1,0", GameToolHotkeys.Format([HotkeyKeys.Control, HotkeyKeys.Shift, 36], "vk-array"));
        Assert.True(GameToolHotkeys.TryParse("Ctrl+Shift+Backspace", "named", out var keys));
        Assert.Equal("Ctrl+Shift+Backspace", GameToolHotkeys.Format(keys, "named"));
        Assert.Throws<GameToolException>(() => GameToolHotkeys.Format([HotkeyKeys.Windows, 36], "vk-array"));
    }

    [Fact]
    public async Task AdoptionRecordsMatchingDllsWithoutRewritingAndRemovesOnlyThoseBytes()
    {
        using var directory = new TemporaryDirectory();
        var tool = GameToolCatalog.Current.Find("dxvk")!;
        var game = Path.Combine(directory.Root, "game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "d3d11.dll"), "native one");
        File.WriteAllText(Path.Combine(game, "dxgi.dll"), "native two");
        var stamp = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(Path.Combine(game, "d3d11.dll"), stamp);
        var package = Package(directory.Root, tool, "manual", "x64");
        File.WriteAllText(Path.Combine(package.Root, tool.PrimaryFile), "native one");
        File.WriteAllText(Path.Combine(package.Root, "dxgi.dll"), "native two");
        var inspection = new GameToolInspection([new("d3d11.dll", "local"), new("dxgi.dll", "local")],
            [new("d3d11.dll", "", false, "Existing", true)], [], []);
        var target = new GameToolTarget(218, game, Path.Combine(game, "game.exe"), "x64", "fixture");
        var installer = new GameToolInstaller(directory.Root);
        var installed = await installer.InstallAsync(tool, target, package,
            new GameToolRequest("adopt", "d3d11.dll", new Dictionary<string, string>()), null, () => null, _ => { }, CancellationToken.None, inspection);
        Assert.True(installed.Manifest!.Adopted);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(game, "d3d11.dll")));
        Assert.DoesNotContain(installed.Manifest.Replaced, file => file.RelativePath.EndsWith(".dll", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(game, "dxgi.dll"), "a user replacement");
        installer.Remove(tool, target, installed.Manifest);
        Assert.False(File.Exists(Path.Combine(game, "d3d11.dll")));
        Assert.Equal("a user replacement", File.ReadAllText(Path.Combine(game, "dxgi.dll")));
    }

    [Fact]
    public async Task SharedPluginSurvivesOneRemovalAndDisabledConsumersThenRestoresAfterLastRemoval()
    {
        using var directory = new TemporaryDirectory();
        var shared = new GameToolSharedPlugins(directory.Root);
        var tool = GameToolSharedPlugins.Definition(GameToolCatalog.Current.Find("special-k")!);
        var folder = Path.Combine(directory.Root, "plugin");
        var one = Package(directory.Root, tool, "one", "x86");
        await shared.EnsureAsync(1, folder, tool, one, () => null, CancellationToken.None);
        Reference(directory.Root, 1, folder, enabled: true);
        await shared.EnsureAsync(2, folder, tool, one, () => null, CancellationToken.None);
        Reference(directory.Root, 2, folder, enabled: false);
        var two = Package(directory.Root, tool, "two", "x86");
        await Assert.ThrowsAsync<GameToolException>(() => shared.EnsureAsync(2, folder, tool, two,
            () => "Another game is running", CancellationToken.None));
        Assert.Equal("one", File.ReadAllText(Path.Combine(folder, "DDraw.dll")));
        await shared.EnsureAsync(2, folder, tool, two, () => null, CancellationToken.None);
        Assert.Equal("two", File.ReadAllText(Path.Combine(folder, "DDraw.dll")));
        GameToolStorage.Mutate(directory.Root, store => store.With(1, null));
        shared.Reconcile(_ => null);
        Assert.True(File.Exists(Path.Combine(folder, "DDraw.dll")));
        Assert.Equal([2u], Assert.Single(GameToolStorage.Read(directory.Root).SharedPlugins).Consumers);
        GameToolStorage.Mutate(directory.Root, store => store.With(2, null));
        shared.Reconcile(_ => null);
        Assert.False(File.Exists(Path.Combine(folder, "DDraw.dll")));
        Assert.Empty(GameToolStorage.Read(directory.Root).SharedPlugins);
    }

    [Fact]
    public async Task UserSharedPluginIsIdentifiedBorrowedAndNeverUpdatedOrDeleted()
    {
        using var directory = new TemporaryDirectory();
        var tool = GameToolSharedPlugins.Definition(GameToolCatalog.Current.Find("special-k")!);
        var folder = Path.Combine(directory.Root, "plugin");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "DDraw.dll", "D3D8.dll", "D3DImm.dll" })
        {
            File.WriteAllText(Path.Combine(folder, name), "user bytes");
        }
        var shared = new GameToolSharedPlugins(directory.Root, path => new GameToolBinary(Path.GetFileName(path),
            "dgvoodoo2", "dgVoodoo2", "x86", [], "2.87.6", "Fixture publisher/exports"));
        await shared.EnsureAsync(1, folder, tool, Package(directory.Root, tool, "new", "x86"), () => null, CancellationToken.None);
        Assert.True(Assert.Single(GameToolStorage.Read(directory.Root).SharedPlugins).External);
        shared.Reconcile(_ => null);
        Assert.Equal("user bytes", File.ReadAllText(Path.Combine(folder, "DDraw.dll")));
    }

    [Fact]
    public async Task SharedHotkeysPreserveUnknownKeysAndRestorePreexistingOsdFile()
    {
        using var directory = new TemporaryDirectory();
        var folder = Path.Combine(directory.Root, "Global");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "osd.ini");
        var original = "[Custom]\nUnknown=keep\n";
        File.WriteAllText(file, original);
        var manager = new GameToolSharedPlugins(directory.Root);
        manager.Register(folder, 1);
        await manager.ConfigureAsync(folder, "osd.ini", GameToolCatalog.Current.Find("special-k")!,
            new Dictionary<string, string> { ["controlKey"] = "Ctrl+F2" }, "x64", () => null);
        Assert.Contains("Unknown=keep", File.ReadAllText(file));
        Assert.Equal("Ctrl+F2", GameToolIni.Read(file, [("ImGui.Global", "ControlPanelToggle")])["imgui.global/controlpaneltoggle"]);
        manager.Reconcile(_ => null);
        Assert.Equal(original, File.ReadAllText(file));
    }

    private static void Reference(string root, uint id, string folder, bool enabled)
    {
        GameToolStorage.Mutate(root, store => store.With(id, new GameToolRecord { AppId = id,
            Tools = [new GameToolManifest { ToolId = "special-k", Enabled = enabled, SharedDependencyDirectory = folder }] }));
    }

    [Fact]
    public async Task WorkingDirectoryAndRuntimeConfigsShareValuesButPreserveTheirOwnUnknownKeys()
    {
        using var directory = new TemporaryDirectory();
        var data = Path.Combine(directory.Root, "data");
        var working = Path.Combine(directory.Root, "game");
        var runtime = Path.Combine(working, "Win64");
        Directory.CreateDirectory(runtime);
        var originalWorking = "# working\nworking.unknown = 4\n";
        var originalRuntime = "# runtime\nruntime.unknown = 5\n";
        File.WriteAllText(Path.Combine(working, "dxvk.conf"), originalWorking);
        File.WriteAllText(Path.Combine(runtime, "dxvk.conf"), originalRuntime);
        var tool = GameToolCatalog.Current.Find("dxvk")!;
        var shared = new GameToolSharedPlugins(data);
        shared.Register(working, 218);
        await shared.ConfigureAsync(working, "dxvk.conf", tool with
        {
            Settings = [.. tool.Settings.Select(setting => setting with { Scope = "shared" })]
        }, new Dictionary<string, string> { ["dxgiFps"] = "45" }, "x64", () => null);
        var native = GameToolInstaller.ReadSettings(tool, working)!;
        var target = new GameToolTarget(218, working, Path.Combine(runtime, "game.exe"), "x64", "fixture");
        var installer = new GameToolInstaller(data);
        var package = Package(directory.Root, tool, "mirrored", "x64") with { SharedSettingsDirectory = working };
        var installed = await installer.InstallAsync(tool, target, package,
            new GameToolRequest("install", "d3d11.dll", new Dictionary<string, string> { ["dxgiFps"] = native["dxgiFps"] }),
            null, () => null, _ => { }, CancellationToken.None);
        Assert.Equal("45", GameToolInstaller.ReadSettings(tool, runtime)!["dxgiFps"]);
        Assert.Contains("working.unknown = 4", File.ReadAllText(Path.Combine(working, "dxvk.conf")));
        Assert.Contains("runtime.unknown = 5", File.ReadAllText(Path.Combine(runtime, "dxvk.conf")));
        installer.Remove(tool, target, installed.Manifest!);
        shared.Reconcile(_ => null);
        Assert.Equal(originalWorking, File.ReadAllText(Path.Combine(working, "dxvk.conf")));
        Assert.Equal(originalRuntime, File.ReadAllText(Path.Combine(runtime, "dxvk.conf")));
    }

    [Fact]
    public async Task PendingSharedUpdateRollsBackBeforeConsumerUse()
    {
        using var directory = new TemporaryDirectory();
        var shared = new GameToolSharedPlugins(directory.Root);
        var tool = GameToolSharedPlugins.Definition(GameToolCatalog.Current.Find("special-k")!);
        var folder = Path.Combine(directory.Root, "plugin");
        await shared.EnsureAsync(1, folder, tool, Package(directory.Root, tool, "old", "x86"), () => null, CancellationToken.None);
        Reference(directory.Root, 1, folder, enabled: true);
        var record = Assert.Single(GameToolStorage.Read(directory.Root).SharedPlugins);
        var file = Path.Combine(folder, "D3D8.dll");
        var backup = Path.Combine(directory.Root, "backup.dll");
        File.Copy(file, backup);
        File.WriteAllText(file, "interrupted update");
        var pending = record.Manifest! with
        {
            State = GameToolManifestState.Pending, Previous = record.Manifest,
            Journal = [new GameToolJournalEntry { RelativePath = "D3D8.dll", Action = GameToolJournalAction.Replace,
                BackupPath = backup, PreviousSha256 = GameToolInstaller.Sha256(backup), NewSha256 = GameToolInstaller.Sha256(file) }]
        };
        GameToolStorage.Mutate(directory.Root, store => store with { SharedPlugins = [record with { Manifest = pending }] });
        shared.Reconcile(_ => null);
        Assert.Equal("old", File.ReadAllText(file));
        Assert.Equal(GameToolManifestState.Installed, Assert.Single(GameToolStorage.Read(directory.Root).SharedPlugins).Manifest!.State);
    }

    [Fact]
    public void SharedPluginLocationMatchesTheDocumentedUpstreamPrecedence()
    {
        using var directory = new TemporaryDirectory();
        var environment = Path.Combine(directory.Root, "environment");
        var registry = Path.Combine(directory.Root, "registry");
        var docs = Path.Combine(directory.Root, "documents");
        Assert.Equal(environment, SpecialKPluginLocation.Resolve(environment, registry, docs, create: false));
        Assert.Equal(registry, SpecialKPluginLocation.Resolve(null, registry, docs, create: false));
        Assert.Equal(Path.Combine(docs, "My Mods", "SpecialK"), SpecialKPluginLocation.Resolve(null, null, docs, create: false));
        Assert.Throws<GameToolException>(() => SpecialKPluginLocation.Resolve("relative", registry, docs, create: false));
    }

    internal static GameToolPackage Package(string root, GameToolDefinition tool, string version, string architecture)
    {
        var payload = Path.Combine(root, "payload-" + version + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, tool.PrimaryFile), version);
        foreach (var file in tool.InstallMode(tool.Proxy.Default).Files.Values.Where(file => file != tool.PrimaryFile))
        {
            File.WriteAllText(Path.Combine(payload, file), tool.IsConfig(file) ? "Version=0x287\n" : version);
        }
        var hashes = Directory.EnumerateFiles(payload).ToDictionary(file => Path.GetFileName(file)!,
            file => (GameToolInstaller.Sha256(file), new FileInfo(file).Length), StringComparer.OrdinalIgnoreCase);
        var release = new HostedRelease(version, version, "", "Fixture", new ReleaseAsset("fixture.zip", "https://example.test/fixture.zip"));
        return new GameToolPackage(release, payload, version, "", architecture) { Hashes = hashes };
    }
}
