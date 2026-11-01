using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core;

public sealed class GameToolInstallerTests
{
    private const uint AppId = 1_091_500;
    private static readonly GameToolDefinition Tool = GameToolCatalog.Current.Find("optiscaler")!;

    [Fact]
    public async Task SwitchingFromNightlyToAnOlderStableReplacesTheV10LayoutAndRecordsTheChannel()
    {
        using var directory = new TemporaryDirectory();
        var game = Path.Combine(directory.Root, "game");
        Directory.CreateDirectory(game);
        await File.WriteAllTextAsync(Path.Combine(game, "Game.exe"), "program");
        var target = new GameToolTarget(AppId, game, Path.Combine(game, "Game.exe"), "x64", "unknown");
        var installer = new GameToolInstaller(Path.Combine(directory.Root, "data"));
        var nightly = Package(directory.Root, "nightly", "10.0.0-pre1", "v10",
            "OptiScaler_v10.0.0-pre1_20261031.7z",
            ["OptiScaler/libxess.dll", "OptiScaler/D3D12_OptiScaler/D3D12Core.dll"]);
        List<string> phases = [];

        var installed = await installer.InstallAsync(Tool, target, nightly,
            new GameToolRequest("install", "dxgi.dll", new Dictionary<string, string>(), Channel: "nightly"), null,
            () => null, phases.Add, CancellationToken.None);

        Assert.Equal("nightly", installed.Manifest!.Channel);
        Assert.Equal("OptiScaler 10.0.0-pre1 · nightly 31 Oct 2026 installed as dxgi.dll.", installed.Notice);
        Assert.Equal([GameToolPhase.Copy, GameToolPhase.Settings], phases);
        Assert.True(File.Exists(Path.Combine(game, "OptiScaler", "D3D12_OptiScaler", "D3D12Core.dll")));
        var stored = GameToolStorage.Read(Path.Combine(directory.Root, "data")).Find(AppId)!.Manifest("optiscaler")!;
        Assert.Equal("nightly", stored.Channel);

        var stable = Package(directory.Root, "stable", "0.9.4", "v09", "Optiscaler_0.9.4-final.20260718._MM.7z",
            ["libxess.dll"]);

        // The older stable uses another configuration format, so the switch needs the user's confirmation.
        await Assert.ThrowsAsync<GameToolException>(() => installer.InstallAsync(Tool, target, stable,
            new GameToolRequest("switchChannel", "dxgi.dll", new Dictionary<string, string>(), Channel: "stable"),
            stored, () => null, _ => { }, CancellationToken.None));
        Assert.Equal("nightly", GameToolStorage.Read(Path.Combine(directory.Root, "data")).Find(AppId)!
            .Manifest("optiscaler")!.Channel);

        var switched = await installer.InstallAsync(Tool, target, stable,
            new GameToolRequest("switchChannel", "dxgi.dll", new Dictionary<string, string>(), true, "stable"),
            stored, () => null, _ => { }, CancellationToken.None);

        Assert.Equal("OptiScaler switched to stable 0.9.4.", switched.Notice);
        var manifest = GameToolStorage.Read(Path.Combine(directory.Root, "data")).Find(AppId)!
            .Manifest("optiscaler")!;
        Assert.Equal("stable", manifest.Channel);
        Assert.Equal("0.9.4", GameToolVersions.Installed(manifest));
        Assert.Equal("v09", manifest.Family);
        Assert.Equal("stable", await File.ReadAllTextAsync(Path.Combine(game, "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(game, "libxess.dll")));
        Assert.False(File.Exists(Path.Combine(game, "OptiScaler", "D3D12_OptiScaler", "D3D12Core.dll")));
        Assert.False(File.Exists(Path.Combine(game, "OptiScaler", "libxess.dll")));
    }

    [Fact]
    public void ASavedSettingNamesItselfAndItsFileWithoutClaimingWhenItApplies()
    {
        Assert.Equal("Menu scale saved to OptiScaler.ini.",
            GameToolInstaller.SavedNotice(Tool, ["menuScale"], []));
        Assert.Equal("Menu scale and hide the startup message saved to OptiScaler.ini.",
            GameToolInstaller.SavedNotice(Tool, ["menuScale", "hideSplash"], []));
        Assert.Equal("DLSS inputs on AMD and Intel saved to OptiScaler.ini.",
            GameToolInstaller.SavedNotice(Tool, ["dlssInputs"], []));
        Assert.Equal("Not available in this version: Menu scale.",
            GameToolInstaller.SavedNotice(Tool, ["menuScale"], ["Menu scale"]));
    }

    private static GameToolPackage Package(string root, string name, string version, string family, string asset,
        IReadOnlyList<string> bundled)
    {
        var payload = Path.Combine(root, "packages", name);
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "OptiScaler.dll"), name);
        File.WriteAllText(Path.Combine(payload, "OptiScaler.ini"),
            "[Menu]\nScale=auto\nDisableSplash=auto\n[Spoofing]\nDxgi=auto\n");
        foreach (var file in bundled)
        {
            var path = Path.Combine(payload, file.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, name + ":" + file);
        }

        var release = new HostedRelease(version, name + ":1:" + new string('a', 64), "", "GitHub",
            new ReleaseAsset(asset, "https://example.test/" + asset, new string('a', 64)));
        return new GameToolPackage(release, payload, version, family, "x64");
    }
}
