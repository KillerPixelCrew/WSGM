using WSGM.Device.Tests;
using WSGM.Plugin.Sdk;
using Xunit;
using static WSGM.Plugin.Ir.Tests.IrActions;

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
        using TemporaryDirectory temporary = new();
        PluginContext context = Context(temporary.Root);
        await using IrPlugin plugin = new(_ => endpoint);
        await plugin.StartAsync(new RecordingPluginHost(), context, default);
        await plugin.ConfigureAsync(Configuration(port: "COM3"), context, default);
        await body(plugin, context);
    }

    [Fact]
    public async Task OlderFirmwareCarriesNoRemotesAndIsRefusedWithoutEmitting()
    {
        FakeEndpoint endpoint = new() { Firmware = "0.3.0", Catalog = new([]) };

        await WithPlugin(endpoint, async (plugin, context) =>
        {
            PluginActionResult result = await InvokeAutomated(plugin, context, "remote-press",
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
            PluginActionResult result = await InvokeAutomated(plugin, context, "remote-press",
                ("remote", new(Text: "hdmi-switch")), ("button", new(Text: "port-1")));

            Assert.Equal(PluginActionOutcome.Dispatched, result.Outcome);
            Assert.Equal(["press hdmi-switch/port-1"], endpoint.RemoteCalls);
            // The catalog was unknown, so it was read once; the next press reuses it.
            Assert.Equal(1, endpoint.CatalogReads);
            await InvokeAutomated(plugin, context, "remote-press",
                ("remote", new(Text: "hdmi-switch")), ("button", new(Text: "power")));
            Assert.Equal(1, endpoint.CatalogReads);
        });
    }

    [Fact]
    public async Task APostPressPauseKeepsTheNextButtonBehindThePowerOffInterval()
    {
        FakeEndpoint endpoint = new() { Catalog = Catalog() };
        await WithPlugin(endpoint, async (plugin, context) =>
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            Task<PluginActionResult> off = InvokeAutomated(plugin, context, "remote-press",
                ("remote", new(Text: "hdmi-switch")), ("button", new(Text: "power")),
                ("delay-ms", new(Number: 3000))).AsTask();
            Task<PluginActionResult> on = InvokeAutomated(plugin, context, "remote-press",
                ("remote", new(Text: "hdmi-switch")), ("button", new(Text: "power"))).AsTask();

            Assert.False(off.IsCompleted);
            Assert.False(on.IsCompleted);
            Assert.Single(endpoint.RemoteCalls);
            Assert.Equal(PluginActionOutcome.Dispatched, (await off).Outcome);
            Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(3000));
            Assert.Equal(PluginActionOutcome.Dispatched, (await on).Outcome);
            Assert.Equal(["press hdmi-switch/power", "press hdmi-switch/power"], endpoint.RemoteCalls);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WifiReturnUsesAFreshIdentifiedConnectionAndNeverRetriesAPress(bool failReturn)
    {
        using TemporaryDirectory temporary = new();
        PluginContext context = Context(temporary.Root);
        FakeEndpoint entry = new() { Catalog = Catalog() };
        FakeEndpoint leave = new() { Catalog = Catalog(), FailPress = failReturn };
        int opened = 0;
        await new IrPairing(new string('a', 48), "test.invalid", "192.0.2.1")
            .SaveAsync(Path.Combine(temporary.Root, "endpoint.json"), default);
        await using IrPlugin plugin = new(_ => ++opened == 1 ? entry : leave);
        await plugin.StartAsync(new RecordingPluginHost(), context, default);
        await plugin.ConfigureAsync(Configuration(transport: "wifi"), context, default);
        Assert.Equal(PluginActionOutcome.Dispatched, (await InvokeAutomated(plugin, context, "remote-press",
            ("remote", new(Text: "hdmi-switch")), ("button", new(Text: "port-1")))).Outcome);
        // Simulate the firmware's idle close while the old identity remains cached.
        entry.FailPress = true;
        Assert.NotNull(entry.Identity);
        PluginActionResult result = await InvokeAutomated(plugin, context, "remote-press",
            ("remote", new(Text: "hdmi-switch")), ("button", new(Text: "port-3")));
        Assert.Equal(failReturn ? PluginActionOutcome.Unconfirmed : PluginActionOutcome.Dispatched, result.Outcome);
        Assert.True(entry.Disposed);
        Assert.Equal(2, opened);
        Assert.Equal(1, leave.Identifications);
        Assert.Equal(["press hdmi-switch/port-1"], entry.RemoteCalls);
        Assert.Equal(["press hdmi-switch/port-3"], leave.RemoteCalls);
    }

    [Fact]
    public async Task CancellingAPostPressPauseDoesNotRepeatTheEmittedButton()
    {
        FakeEndpoint endpoint = new() { Catalog = Catalog() };
        await WithPlugin(endpoint, async (plugin, context) =>
        {
            using CancellationTokenSource cancellation = new();
            Task<PluginActionResult> press = plugin.ExecuteActionAsync(new(Guid.NewGuid(), "remote-press",
                PluginActionOrigin.SessionAutomation, Arguments(plugin, "remote-press",
                    ("remote", new(Text: "hdmi-switch")), ("button", new(Text: "power")),
                    ("delay-ms", new(Number: 3000)))), context, cancellation.Token).AsTask();
            Assert.Single(endpoint.RemoteCalls);
            Assert.False(press.IsCompleted);
            cancellation.Cancel();
            Assert.Equal(PluginActionOutcome.Unconfirmed, (await press).Outcome);
            Assert.Single(endpoint.RemoteCalls);
        });
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5001)]
    [InlineData(0.5)]
    public async Task InvalidPostPressPauseDoesNotEmit(double delay)
    {
        FakeEndpoint endpoint = new() { Catalog = Catalog() };
        await WithPlugin(endpoint, async (plugin, context) =>
        {
            PluginActionResult result = await InvokeAutomated(plugin, context, "remote-press",
                ("remote", new(Text: "hdmi-switch")), ("button", new(Text: "power")),
                ("delay-ms", new(Number: delay)));
            Assert.Equal(PluginActionOutcome.Unconfirmed, result.Outcome);
            Assert.Empty(endpoint.RemoteCalls);
        });
    }

    [Fact]
    public async Task AnUnknownIdIsRefusedAfterARefreshRatherThanEmitted()
    {
        FakeEndpoint endpoint = new() { Catalog = Catalog() };

        await WithPlugin(endpoint, async (plugin, context) =>
        {
            PluginActionResult unknownRemote = await InvokeAutomated(plugin, context, "remote-press",
                ("remote", new(Text: "nope")), ("button", new(Text: "port-1")));
            Assert.Equal(PluginActionOutcome.Rejected, unknownRemote.Outcome);
            Assert.Contains("no remote", unknownRemote.Detail);

            PluginActionResult unknownButton = await InvokeAutomated(plugin, context, "remote-press",
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
            PluginActionResult tooWarm = await InvokeAutomated(plugin, context, "remote-climate",
                ("remote", new(Text: "koenic-ac")), ("degrees", new(Number: 45)));
            Assert.Equal(PluginActionOutcome.Rejected, tooWarm.Outcome);
            Assert.Contains("17-30", tooWarm.Detail);

            PluginActionResult noHeat = await InvokeAutomated(plugin, context, "remote-climate",
                ("remote", new(Text: "koenic-ac")), ("mode", new(Text: "heat")));
            Assert.Equal(PluginActionOutcome.Rejected, noHeat.Outcome);

            PluginActionResult notAnAc = await InvokeAutomated(plugin, context, "remote-climate",
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
            PluginActionResult result = await InvokeAutomated(plugin, context, "remote-climate",
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
            PluginActionResult result = await InvokeAutomated(plugin, context, "remote-run",
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
        RecordingPluginHost host = new();
        using TemporaryDirectory temporary = new();
        PluginContext context = Context(temporary.Root);
        await using IrPlugin plugin = new(_ => endpoint);
        await plugin.StartAsync(host, context, default);
        await plugin.ConfigureAsync(Configuration(port: "COM3"), context, default);

        Assert.Equal(PluginActionOutcome.AppliedVerified,
            (await InvokeAutomated(plugin, context, "remote-refresh")).Outcome);

        string published = host.States.Last(state => state.Key == "remotes").Value.Text ?? "";
        Assert.Contains("hdmi-switch", published);
        Assert.Contains("android-audio-reset", published);
        Assert.Contains("climate", published);
    }
}
