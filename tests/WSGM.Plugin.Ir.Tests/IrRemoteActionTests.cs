using WSGM.Plugin.Sdk;
using Xunit;

namespace WSGM.Plugin.Ir.Tests;

/// <summary>The endpoint owns its built-in remotes, so these check that the plugin only ever asks
/// for ids the endpoint declares, and that a refusal is explained rather than emitted.</summary>
public sealed class IrRemoteActionTests
{
    private static IrRemoteCatalog Catalog() => new([
        new("hdmi-switch", "HDMI switch",
            [new("port-1", "Port 1 (PC)"), new("port-3", "Port 3 (Android box)"), new("power", "Power")],
            [new("android-audio-reset", "Android box with audio reset")]),
        new("koenic-ac", "Koenic KAC 12020", [new("off", "Off")], [],
            new("MIDEA", ["cool", "auto", "fan", "dry"], ["auto", "low", "medium", "high"], 17, 30,
                Celsius: true, Swing: "toggle")),
    ]);

    private static async Task WithPlugin(FakeEndpoint endpoint,
        Func<IrPlugin, PluginContext, Task> body)
    {
        string folder = Path.Combine(Path.GetTempPath(), "wsgm-ir-tests-" + Guid.NewGuid().ToString("N"));
        PluginContext context = new(new("wsgm.ir", "test"), 1, PluginSessionMode.Desktop,
            DateTimeOffset.UtcNow.AddMinutes(1), folder);
        try
        {
            await using IrPlugin plugin = new(_ => endpoint);
            await plugin.StartAsync(new Host(), context, default);
            await plugin.ConfigureAsync(Configuration(port: "COM3"), context, default);
            await body(plugin, context);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    [Fact]
    public async Task OlderFirmwareCarriesNoRemotesAndIsRefusedWithoutEmitting()
    {
        FakeEndpoint endpoint = new() { Firmware = "0.3.0", Catalog = new([]) };

        await WithPlugin(endpoint, async (plugin, context) =>
        {
            PluginActionResult result = await Invoke(plugin, context, "remote-press",
                ("remote", new(Text: "hdmi-switch")), ("button", new(Text: "port-1")));

            Assert.Equal(PluginActionOutcome.Rejected, result.Outcome);
            Assert.Contains("0.4.0", result.Detail);
            Assert.Empty(endpoint.RemoteCalls);
        });
    }

    [Fact]
    public async Task AKnownButtonIsPressedAfterOneCatalogRead()
    {
        FakeEndpoint endpoint = new() { Catalog = Catalog() };

        await WithPlugin(endpoint, async (plugin, context) =>
        {
            PluginActionResult result = await Invoke(plugin, context, "remote-press",
                ("remote", new(Text: "hdmi-switch")), ("button", new(Text: "port-1")));

            Assert.Equal(PluginActionOutcome.Dispatched, result.Outcome);
            Assert.Equal(["press hdmi-switch/port-1"], endpoint.RemoteCalls);
            // The catalog was unknown, so it was read once; the next press reuses it.
            Assert.Equal(1, endpoint.CatalogReads);
            await Invoke(plugin, context, "remote-press",
                ("remote", new(Text: "hdmi-switch")), ("button", new(Text: "power")));
            Assert.Equal(1, endpoint.CatalogReads);
        });
    }

    [Fact]
    public async Task AnUnknownIdIsRefusedAfterARefreshRatherThanEmitted()
    {
        FakeEndpoint endpoint = new() { Catalog = Catalog() };

        await WithPlugin(endpoint, async (plugin, context) =>
        {
            PluginActionResult unknownRemote = await Invoke(plugin, context, "remote-press",
                ("remote", new(Text: "nope")), ("button", new(Text: "port-1")));
            Assert.Equal(PluginActionOutcome.Rejected, unknownRemote.Outcome);
            Assert.Contains("no remote", unknownRemote.Detail);

            PluginActionResult unknownButton = await Invoke(plugin, context, "remote-press",
                ("remote", new(Text: "hdmi-switch")), ("button", new(Text: "nope")));
            Assert.Equal(PluginActionOutcome.Rejected, unknownButton.Outcome);
            Assert.Contains("no button", unknownButton.Detail);

            Assert.Empty(endpoint.RemoteCalls);
        });
    }

    [Fact]
    public async Task AClimateStateOutsideWhatTheRemoteDeclaresIsRefused()
    {
        FakeEndpoint endpoint = new() { Catalog = Catalog() };

        await WithPlugin(endpoint, async (plugin, context) =>
        {
            PluginActionResult tooWarm = await Invoke(plugin, context, "remote-climate",
                ("remote", new(Text: "koenic-ac")), ("degrees", new(Number: 45)));
            Assert.Equal(PluginActionOutcome.Rejected, tooWarm.Outcome);
            Assert.Contains("17-30", tooWarm.Detail);

            PluginActionResult noHeat = await Invoke(plugin, context, "remote-climate",
                ("remote", new(Text: "koenic-ac")), ("mode", new(Text: "heat")));
            Assert.Equal(PluginActionOutcome.Rejected, noHeat.Outcome);

            PluginActionResult notAnAc = await Invoke(plugin, context, "remote-climate",
                ("remote", new(Text: "hdmi-switch")));
            Assert.Equal(PluginActionOutcome.Rejected, notAnAc.Outcome);
            Assert.Contains("not an air conditioner", notAnAc.Detail);

            Assert.Empty(endpoint.RemoteCalls);
        });
    }

    [Fact]
    public async Task ADeclaredClimateStateIsSentWithItsSwingToggle()
    {
        FakeEndpoint endpoint = new() { Catalog = Catalog() };

        await WithPlugin(endpoint, async (plugin, context) =>
        {
            PluginActionResult result = await Invoke(plugin, context, "remote-climate",
                ("remote", new(Text: "koenic-ac")), ("mode", new(Text: "cool")),
                ("degrees", new(Number: 20)), ("fan", new(Text: "medium")),
                ("toggle-swing", new(Boolean: true)));

            Assert.Equal(PluginActionOutcome.Dispatched, result.Outcome);
            Assert.Equal(["climate koenic-ac power=True cool 20 medium swing=True"], endpoint.RemoteCalls);
        });
    }

    [Fact]
    public async Task ASequenceWaitsForTheEndpointToFinishIt()
    {
        FakeEndpoint endpoint = new() { Catalog = Catalog(), SequencePolls = 2 };

        await WithPlugin(endpoint, async (plugin, context) =>
        {
            PluginActionResult result = await Invoke(plugin, context, "remote-run",
                ("remote", new(Text: "hdmi-switch")), ("sequence", new(Text: "android-audio-reset")));

            Assert.Equal(PluginActionOutcome.Dispatched, result.Outcome);
            Assert.Equal(["run hdmi-switch/android-audio-reset"], endpoint.RemoteCalls);
            Assert.Equal(0, endpoint.SequencePolls);
            Assert.False(endpoint.Cancelled);
        });
    }

    [Fact]
    public async Task CancellingAWaitCancelsTheSequenceRatherThanRetryingIt()
    {
        FakeEndpoint endpoint = new() { Catalog = Catalog(), SequencePolls = 1000 };
        using CancellationTokenSource cancellation = new();

        await WithPlugin(endpoint, async (plugin, context) =>
        {
            Dictionary<string, PluginValue> arguments = Arguments(plugin, "remote-run",
                ("remote", new(Text: "hdmi-switch")), ("sequence", new(Text: "android-audio-reset")));
            ValueTask<PluginActionResult> running = plugin.ExecuteActionAsync(
                new(Guid.NewGuid(), "remote-run", PluginActionOrigin.SessionAutomation, arguments),
                context, cancellation.Token);
            await cancellation.CancelAsync();
            PluginActionResult result = await running;

            Assert.Equal(PluginActionOutcome.Unconfirmed, result.Outcome);
            Assert.True(endpoint.Cancelled);
        });
    }

    [Fact]
    public async Task ReadingTheRemotesPublishesTheIdsAnActionTakes()
    {
        FakeEndpoint endpoint = new() { Catalog = Catalog() };
        Host host = new();
        string folder = Path.Combine(Path.GetTempPath(), "wsgm-ir-tests-" + Guid.NewGuid().ToString("N"));
        PluginContext context = new(new("wsgm.ir", "test"), 1, PluginSessionMode.Desktop,
            DateTimeOffset.UtcNow.AddMinutes(1), folder);
        try
        {
            await using IrPlugin plugin = new(_ => endpoint);
            await plugin.StartAsync(host, context, default);
            await plugin.ConfigureAsync(Configuration(port: "COM3"), context, default);

            Assert.Equal(PluginActionOutcome.AppliedVerified,
                (await Invoke(plugin, context, "remote-refresh")).Outcome);

            string published = host.States.Last(state => state.Key == "remotes").Value.Text ?? "";
            Assert.Contains("hdmi-switch", published);
            Assert.Contains("android-audio-reset", published);
            Assert.Contains("climate", published);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    private static Dictionary<string, PluginValue> Arguments(IrPlugin plugin, string action,
        params (string Key, PluginValue Value)[] changes)
    {
        Dictionary<string, PluginValue> arguments = plugin.Actions.Single(item => item.Id == action)
            .Arguments.ToDictionary(item => item.Key, item => item.Default);
        foreach (var change in changes) { arguments[change.Key] = change.Value; }
        return arguments;
    }

    private static ValueTask<PluginActionResult> Invoke(IrPlugin plugin, PluginContext context, string action,
        params (string Key, PluginValue Value)[] changes) =>
        plugin.ExecuteActionAsync(
            new(Guid.NewGuid(), action, PluginActionOrigin.SessionAutomation, Arguments(plugin, action, changes)),
            context, default);

    private static PluginConfiguration Configuration(string port) => new(1, PluginConfigurationOrigin.User,
        new Dictionary<string, PluginValue> { ["port"] = new(Text: port) });

    /// <summary>A host that keeps what the plugin published, so a test can read the ids it offers.</summary>
    private sealed class Host : IPluginHost
    {
        internal List<PluginStatePublication> States { get; } = [];
        public void PublishHealth(PluginHealthPublication publication) { }
        public void PublishState(PluginStatePublication publication) => States.Add(publication);
    }
}
