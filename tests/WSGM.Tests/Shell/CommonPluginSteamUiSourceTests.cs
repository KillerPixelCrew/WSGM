using WSGM.Core;
using WSGM.Device.Tests;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class CommonPluginSteamUiSourceTests
{
    [Fact]
    public async Task LongAdmittedNamesRemainWithinTheInjectedMenuLimit()
    {
        using TemporaryDirectory temporary = new();
        var installed = await Catalog(temporary);
        var manifestPath = Path.Combine(installed, "test.steam-plugin", "plugin.wsgm.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath,
            manifest.Replace("\"name\":\"Fixture\"", "\"name\":\"" + new string('P', 128) + "\""));
        PluginHost host = new(action => action());
        SteamUiFixturePlugin plugin = new() { MenuLabel = new string('A', 128) };
        CommonPluginManager manager = new(host, installed, temporary.GetPath("state"),
            (_, _) => Task.FromResult<IPlugin>(plugin));
        using CommonPluginSteamUiSource source = new(manager, host);
        await manager.ReconcileAsync([new CommonPluginInstanceConfig { PluginId = plugin.Id, Enabled = true }],
            CancellationToken.None);

        var command = Assert.Single(source.ReadGameContextMenu().Items);
        Assert.InRange(command.Label.Length, 1, 160);
        Assert.EndsWith(plugin.MenuLabel, command.Label, StringComparison.Ordinal);
        Assert.True((await source.ActivateAsync(480, command.Id, CancellationToken.None)).Succeeded);
        await manager.StopAsync(DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task RetiredCommandsCannotTargetAReplacementRegistration()
    {
        using TemporaryDirectory temporary = new();
        var installed = await Catalog(temporary);
        PluginHost host = new(action => action());
        SteamUiFixturePlugin plugin = new();
        CommonPluginManager manager = new(host, installed, temporary.GetPath("state"),
            (_, _) => Task.FromResult<IPlugin>(plugin));
        using CommonPluginSteamUiSource source = new(manager, host);
        CommonPluginInstanceConfig enabled = new() { PluginId = plugin.Id, Enabled = true };
        await manager.ReconcileAsync([enabled], CancellationToken.None);
        var staleId = Assert.Single(source.ReadExtensionsTab().Items).Id;
        await manager.ReconcileAsync([], CancellationToken.None);
        Assert.False((await source.ActivateAsync(staleId, CancellationToken.None)).Succeeded);
        plugin = new SteamUiFixturePlugin();
        await manager.ReconcileAsync([enabled], CancellationToken.None);
        var currentId = Assert.Single(source.ReadExtensionsTab().Items).Id;
        Assert.NotEqual(staleId, currentId);
        Assert.False((await source.ActivateAsync(staleId, CancellationToken.None)).Succeeded);
        Assert.Null(plugin.LastAction);
        Assert.True((await source.ActivateAsync(currentId, CancellationToken.None)).Succeeded);
        await manager.StopAsync(DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task RejectsWrongSurfaceAndStopsNotificationsAfterDisposal()
    {
        using TemporaryDirectory temporary = new();
        var installed = await Catalog(temporary);
        PluginHost host = new(action => action());
        SteamUiFixturePlugin plugin = new();
        CommonPluginManager manager = new(host, installed, temporary.GetPath("state"),
            (_, _) => Task.FromResult<IPlugin>(plugin));
        using CommonPluginSteamUiSource source = new(manager, host);
        var changes = 0;
        source.Changed += () => changes++;
        await manager.ReconcileAsync([new CommonPluginInstanceConfig { PluginId = plugin.Id, Enabled = true }],
            CancellationToken.None);
        Assert.True(changes > 0);
        var extension = Assert.Single(source.ReadExtensionsTab().Items);
        var game = Assert.Single(source.ReadGameContextMenu().Items);
        Assert.False((await source.ActivateAsync(game.Id, CancellationToken.None)).Succeeded);
        Assert.False((await source.ActivateAsync(480, extension.Id, CancellationToken.None)).Succeeded);
        Assert.False((await source.ActivateAsync(0, game.Id, CancellationToken.None)).Succeeded);
        Assert.Null(plugin.LastAction);
        plugin.Outcome = PluginActionOutcome.Rejected;
        var rejection = await source.ActivateAsync(extension.Id, CancellationToken.None);
        Assert.False(rejection.Succeeded);
        Assert.NotNull(rejection.Error);
        source.Dispose();
        var beforeStop = changes;
        Assert.Empty(source.ReadExtensionsTab().Items);
        Assert.False((await source.ActivateAsync(extension.Id, CancellationToken.None)).Succeeded);
        await manager.StopAsync(DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal(beforeStop, changes);
    }

    [Fact]
    public async Task ProjectsDeclaredCommandsAndRoutesTheExactSelectedApp()
    {
        using TemporaryDirectory temporary = new();
        var installed = await Catalog(temporary);
        PluginHost host = new(action => action());
        SteamUiFixturePlugin plugin = new();
        CommonPluginManager manager = new(host, installed, temporary.GetPath("state"),
            (_, _) => Task.FromResult<IPlugin>(plugin));
        CommonPluginSteamUiSource source = new(manager, host);

        await manager.ReconcileAsync([new CommonPluginInstanceConfig { PluginId = plugin.Id, Enabled = true }],
            CancellationToken.None);

        var extensions = source.ReadExtensionsTab();
        var extension = Assert.Single(extensions.Items);
        Assert.Equal("Fixture / Open", extension.Name);
        Assert.Equal("Ready", extension.Status);

        var extensionResult = await source.ActivateAsync(extension.Id, CancellationToken.None);
        Assert.True(extensionResult.Succeeded);
        Assert.Equal("open", plugin.LastAction);

        var gameMenu = source.ReadGameContextMenu();
        var gameCommand = Assert.Single(gameMenu.Items);
        var gameResult = await source.ActivateAsync(480, gameCommand.Id, CancellationToken.None);
        Assert.True(gameResult.Succeeded);
        Assert.Equal("change-artwork", plugin.LastAction);
        Assert.Equal(480, plugin.LastAppId);

        source.Dispose();
        await manager.StopAsync(DateTimeOffset.UtcNow.AddSeconds(5));
    }

    private static async Task<string> Catalog(TemporaryDirectory temporary)
    {
        var installed = temporary.GetPath("plugins");
        var root = Path.Combine(installed, "test.steam-plugin");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "Fixture.dll"), "Metadata fixture");
        await File.WriteAllTextAsync(Path.Combine(root, "plugin.wsgm.json"), """
                                                                             {"id":"test.steam-plugin","name":"Fixture","version":"1.0.0","category":"example.status",
                                                                              "entryAssembly":"Fixture.dll","entryType":"Fixture.Plugin"}
                                                                             """);
        return installed;
    }

    private sealed class SteamUiFixturePlugin : IPlugin, IPluginActions, IPluginSteamUi
    {
        public string? LastAction { get; private set; }
        public double? LastAppId { get; private set; }
        public PluginActionOutcome Outcome { get; set; } = PluginActionOutcome.AppliedVerified;
        public string MenuLabel { get; init; } = "Change artwork";
        public string Id => "test.steam-plugin";

        public ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(PluginHealth.Ready);
        }

        public ValueTask SessionChangedAsync(PluginContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public IReadOnlyList<PluginAction> Actions =>
        [
            new("open", "Open", []),
            new("change-artwork", "Change artwork",
                [new PluginSetting("app-id", "Application id", PluginSettingKind.Number, new PluginValue(Number: 1))])
        ];

        public ValueTask<PluginActionResult> ExecuteActionAsync(PluginActionRequest request, PluginContext context,
            CancellationToken cancellationToken)
        {
            LastAction = request.ActionId;
            LastAppId = request.Arguments.TryGetValue("app-id", out var appId) ? appId.Number : null;
            return ValueTask.FromResult(
                new PluginActionResult(request.OperationId, Outcome));
        }

        public IReadOnlyList<PluginSteamUiContribution> SteamUiContributions =>
        [
            new("open", "Open", PluginSteamUiPlacement.ExtensionsTab, "open"),
            new("change-artwork", MenuLabel,
                PluginSteamUiPlacement.GameContextMenu, "change-artwork", "app-id")
        ];
    }
}
