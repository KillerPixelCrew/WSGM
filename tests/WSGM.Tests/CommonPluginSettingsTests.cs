using System.Text.Json;
using WSGM.Core;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class CommonPluginSettingsTests
{
    private static DateTimeOffset Deadline => DateTimeOffset.UtcNow.AddSeconds(5);

    [Fact]
    public async Task DefaultsAndHardwareReadbackNeverBecomeSavedPreferences()
    {
        MemoryStore store = new();
        PluginHost host = new(action => action(), store);
        Configurable plugin = new();
        var registration = Admit(host, plugin);
        await registration.StartAsync(Deadline, default);
        Assert.Empty(store.Config.PluginConfigurations);
        Assert.Equal(0, registration.Settings!.Desired!.Revision);
        Assert.Equal(PluginConfigurationOrigin.Restore, plugin.Deliveries[0].Origin);
        plugin.Outcome = PluginConfigurationOutcome.Unconfirmed;
        var result = await registration.ConfigureAsync(0, new Dictionary<string, PluginValue> { ["level"] = new(Number: 40) }, Deadline, default);
        Assert.Equal(PluginConfigurationOutcome.Unconfirmed, result.Outcome);
        var saved = Assert.Single(store.Config.PluginConfigurations);
        Assert.Equal(new PluginValue(Number: 40), Assert.Single(saved.Values).Value);
        Assert.Equal(1, saved.Revision);
        Assert.Equal(new PluginValue(Number: 100), Assert.Single(host.StateSnapshot(registration.Identity)).Value);
        Assert.Equal(new PluginValue(Number: 40), registration.Settings.Desired!.Values["level"]);
        await Close(registration);
    }

    [Fact]
    public async Task StaleUserEditsDoNotDispatchAndCanBeCorrectedAfterRefresh()
    {
        MemoryStore store = new();
        PluginHost host = new(action => action(), store);
        Configurable plugin = new();
        var registration = Admit(host, plugin);
        await registration.StartAsync(Deadline, default);
        var changes = new Dictionary<string, PluginValue> { ["level"] = new(Number: 40) };
        await registration.ConfigureAsync(0, changes, Deadline, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registration.ConfigureAsync(0, changes, Deadline, default));
        Assert.Equal(2, plugin.Deliveries.Count);
        Assert.False(registration.Quarantined);
        changes["level"] = new(Number: 30);
        await registration.ConfigureAsync(1, changes, Deadline, default);
        Assert.Equal(2, registration.Settings!.Desired!.Revision);
        await Close(registration);
    }

    [Fact]
    public async Task FailedPersistencePreventsDispatchAndWrongConfirmationKeepsDesiredRevision()
    {
        MemoryStore store = new();
        PluginHost host = new(action => action(), store);
        Configurable plugin = new();
        var registration = Admit(host, plugin);
        await registration.StartAsync(Deadline, default);
        var changes = new Dictionary<string, PluginValue> { ["level"] = new(Number: 40) };
        store.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => registration.ConfigureAsync(0, changes, Deadline, default));
        Assert.Single(plugin.Deliveries);
        Assert.Empty(store.Config.PluginConfigurations);
        store.FailSave = false;
        plugin.WrongRevision = true;
        var result = await registration.ConfigureAsync(0, changes, Deadline, default);
        Assert.Equal(PluginConfigurationOutcome.Unconfirmed, result.Outcome);
        Assert.Equal(1, registration.Settings!.Desired!.Revision);
        Assert.Equal(1, Assert.Single(store.Config.PluginConfigurations).Revision);
        await Close(registration);
    }

    [Fact]
    public void PreferencesRoundTripWithoutLosingFalseZeroOrEmptyText()
    {
        AppConfig config = new();
        var identity = new PluginInstanceIdentity("test.config", "one");
        ApplicationPluginConfigurationStore.SaveInto(config, identity, 0, new Dictionary<string, PluginValue>
        { ["flag"] = new(Boolean: false), ["level"] = new(Number: 0), ["text"] = new(Text: "") });
        string json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig);
        var restored = ApplicationPluginConfigurationStore.ReadFrom(ConfigStore.DeserializeConfig(json), identity);
        Assert.Equal(1, restored.Revision);
        Assert.Equal(false, restored.Values["flag"].Boolean);
        Assert.Equal(0, restored.Values["level"].Number);
        Assert.Equal("", restored.Values["text"].Text);
        Assert.All(restored.Values, value => Assert.True(value.Value.IsValid));
    }

    private static PluginRegistration Admit(PluginHost host, Configurable plugin) =>
        host.Admit(plugin, new(plugin.Id, "one"), PluginCategories.Peripheral, PluginCategoryPolicy.Multiple, false, 1, "fixture-state");

    [Fact]
    public async Task APluginFailureAfterSavingLeavesTheRequestedPreferenceUnconfirmed()
    {
        MemoryStore store = new();
        PluginHost host = new(action => action(), store);
        Configurable plugin = new();
        var registration = Admit(host, plugin);
        await registration.StartAsync(Deadline, default);
        plugin.FailConfiguration = true;
        await Assert.ThrowsAsync<IOException>(() => registration.ConfigureAsync(0,
            new Dictionary<string, PluginValue> { ["level"] = new(Number: 40) }, Deadline, default));
        Assert.Equal(new PluginValue(Number: 40), Assert.Single(store.Config.PluginConfigurations).Values["level"]);
        Assert.Equal(PluginConfigurationOutcome.Unconfirmed, registration.Settings!.Result!.Outcome);
        Assert.Equal(2, plugin.Deliveries.Count);
        await Close(registration);
    }

    private static async Task Close(PluginRegistration registration)
    { Assert.True(await registration.StopAsync(Deadline, default)); await registration.DisposeAsync(); }

    private sealed class MemoryStore : IPluginConfigurationStore
    {
        internal AppConfig Config { get; } = new();
        internal bool FailSave { get; set; }
        public SavedPluginConfiguration Read(PluginInstanceIdentity identity) => ApplicationPluginConfigurationStore.ReadFrom(Config, identity);
        public SavedPluginConfiguration Save(PluginInstanceIdentity identity, long revision, IReadOnlyDictionary<string, PluginValue> changes)
        {
            if (FailSave) { throw new IOException("Fixture persistence failure"); }
            ApplicationPluginConfigurationStore.SaveInto(Config, identity, revision, changes);
            return Read(identity);
        }
    }

    private sealed class Configurable : IPlugin, IConfigurablePlugin
    {
        private IPluginHost? _host;
        private long _sequence;
        public string Id => "test.config";
        public IReadOnlyList<PluginSetting> Settings =>
            [new("level", "Level", PluginSettingKind.Number, new(Number: 20), 0, 100),
             new("enabled", "Enabled", PluginSettingKind.Boolean, new(Boolean: true))];
        internal List<PluginConfiguration> Deliveries { get; } = [];
        internal PluginConfigurationOutcome Outcome { get; set; } = PluginConfigurationOutcome.Applied;
        internal bool WrongRevision { get; set; }
        internal bool FailConfiguration { get; set; }
        public ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context, CancellationToken cancellationToken)
        { _host = host; return ValueTask.FromResult(PluginHealth.Ready); }
        public ValueTask<PluginConfigurationResult> ConfigureAsync(PluginConfiguration configuration, PluginContext context, CancellationToken cancellationToken)
        {
            Deliveries.Add(configuration);
            if (FailConfiguration) { throw new IOException("Fixture configuration failure after dispatch"); }
            _host!.PublishState(new(context.Instance, context.Generation, ++_sequence, "level", new(Number: 100), PluginStateOrigin.HardwareReadback));
            return ValueTask.FromResult(new PluginConfigurationResult(WrongRevision ? configuration.Revision + 1 : configuration.Revision, Outcome));
        }
        public ValueTask SessionChangedAsync(PluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken) => ValueTask.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
