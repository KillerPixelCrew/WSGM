using System.Diagnostics;
using WSGM.Device.Tests;
using WSGM.Plugin.Ir.Tests.Fakes;
using WSGM.Plugin.Sdk;
using Xunit;
using static WSGM.Plugin.Ir.Tests.Builders.IrActions;

namespace WSGM.Plugin.Ir.Tests;

/// <summary>
///     The endpoint owns its built-in remotes, so these check that the plugin only ever asks
///     for ids the endpoint declares, and that a refusal is explained rather than emitted.
/// </summary>
public sealed class IrRemoteActionTests
{
    private static IrRemoteCatalog Catalog()
    {
        return new IrRemoteCatalog([
            new IrRemote("hdmi-switch", "HDMI switch",
                [
                    new IrRemoteButton("port-1", "Port 1 (PC)"), new IrRemoteButton("port-3", "Port 3 (Android box)"),
                    new IrRemoteButton("power", "Power")
                ],
                [new IrRemoteButton("android-audio-reset", "Android box with audio reset")]),
            new IrRemote("koenic-ac", "Koenic KAC 12020", [new IrRemoteButton("off", "Off")], [],
                new IrRemoteClimate("MIDEA", ["cool", "auto", "fan", "dry"], ["auto", "low", "medium", "high"], 17, 30,
                    true, "toggle"))
        ]);
    }

    private static async Task WithPlugin(FakeEndpoint endpoint,
        Func<IrPlugin, PluginContext, Task> body)
    {
        using TemporaryDirectory temporary = new();
        var context = Context(temporary.Root);
        await using IrPlugin plugin = new(_ => endpoint);
        await plugin.StartAsync(new RecordingPluginHost(), context, CancellationToken.None);
        await plugin.ConfigureAsync(Configuration("COM3"), context, CancellationToken.None);
        await body(plugin, context);
    }

    [Fact]
    public async Task OlderFirmwareCarriesNoRemotesAndIsRefusedWithoutEmitting()
    {
        FakeEndpoint endpoint = new() { Firmware = "0.3.0", Catalog = new IrRemoteCatalog([]) };

        await WithPlugin(endpoint, async (plugin, context) =>
        {
            var result = await InvokeAutomated(plugin, context, "remote-press",
                ("remote", new PluginValue(Text: "hdmi-switch")), ("button", new PluginValue(Text: "port-1")));

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
            var result = await InvokeAutomated(plugin, context, "remote-press",
                ("remote", new PluginValue(Text: "hdmi-switch")), ("button", new PluginValue(Text: "port-1")));

            Assert.Equal(PluginActionOutcome.Dispatched, result.Outcome);
            Assert.Equal(["press hdmi-switch/port-1"], endpoint.RemoteCalls);
            // The catalog was unknown, so it was read once; the next press reuses it.
            Assert.Equal(1, endpoint.CatalogReads);
            await InvokeAutomated(plugin, context, "remote-press",
                ("remote", new PluginValue(Text: "hdmi-switch")), ("button", new PluginValue(Text: "power")));
            Assert.Equal(1, endpoint.CatalogReads);
        });
    }

    [Fact]
    public async Task APostPressPauseKeepsTheNextButtonBehindThePowerOffInterval()
    {
        FakeEndpoint endpoint = new() { Catalog = Catalog() };
        await WithPlugin(endpoint, async (plugin, context) =>
        {
            var elapsed = Stopwatch.StartNew();
            var off = InvokeAutomated(plugin, context, "remote-press",
                ("remote", new PluginValue(Text: "hdmi-switch")), ("button", new PluginValue(Text: "power")),
                ("delay-ms", new PluginValue(Number: 3000))).AsTask();
            var on = InvokeAutomated(plugin, context, "remote-press",
                ("remote", new PluginValue(Text: "hdmi-switch")), ("button", new PluginValue(Text: "power"))).AsTask();

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
        var context = Context(temporary.Root);
        FakeEndpoint entry = new() { Catalog = Catalog() };
        FakeEndpoint leave = new() { Catalog = Catalog(), FailPress = failReturn };
        var opened = 0;
        await new IrPairing(new string('a', 48), "test.invalid", "192.0.2.1")
            .SaveAsync(Path.Combine(temporary.Root, "endpoint.json"), CancellationToken.None);
        await using IrPlugin plugin = new(_ => ++opened == 1 ? entry : leave);
        await plugin.StartAsync(new RecordingPluginHost(), context, CancellationToken.None);
        await plugin.ConfigureAsync(Configuration(transport: "wifi"), context, CancellationToken.None);
        Assert.Equal(PluginActionOutcome.Dispatched, (await InvokeAutomated(plugin, context, "remote-press",
            ("remote", new PluginValue(Text: "hdmi-switch")), ("button", new PluginValue(Text: "port-1")))).Outcome);
        // Simulate the firmware's idle close while the old identity remains cached.
        entry.FailPress = true;
        Assert.NotNull(entry.Identity);
        var result = await InvokeAutomated(plugin, context, "remote-press",
            ("remote", new PluginValue(Text: "hdmi-switch")), ("button", new PluginValue(Text: "port-3")));
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
            var press = plugin.ExecuteActionAsync(new PluginActionRequest(Guid.NewGuid(), "remote-press",
                PluginActionOrigin.SessionAutomation, Arguments(plugin, "remote-press",
                    ("remote", new PluginValue(Text: "hdmi-switch")), ("button", new PluginValue(Text: "power")),
                    ("delay-ms", new PluginValue(Number: 3000)))), context, cancellation.Token).AsTask();
            Assert.Single(endpoint.RemoteCalls);
            Assert.False(press.IsCompleted);
            await cancellation.CancelAsync();
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
            var result = await InvokeAutomated(plugin, context, "remote-press",
                ("remote", new PluginValue(Text: "hdmi-switch")), ("button", new PluginValue(Text: "power")),
                ("delay-ms", new PluginValue(Number: delay)));
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
            var unknownRemote = await InvokeAutomated(plugin, context, "remote-press",
                ("remote", new PluginValue(Text: "nope")), ("button", new PluginValue(Text: "port-1")));
            Assert.Equal(PluginActionOutcome.Rejected, unknownRemote.Outcome);
            Assert.Contains("no remote", unknownRemote.Detail);

            var unknownButton = await InvokeAutomated(plugin, context, "remote-press",
                ("remote", new PluginValue(Text: "hdmi-switch")), ("button", new PluginValue(Text: "nope")));
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
            var tooWarm = await InvokeAutomated(plugin, context, "remote-climate",
                ("remote", new PluginValue(Text: "koenic-ac")), ("degrees", new PluginValue(Number: 45)));
            Assert.Equal(PluginActionOutcome.Rejected, tooWarm.Outcome);
            Assert.Contains("17-30", tooWarm.Detail);

            var noHeat = await InvokeAutomated(plugin, context, "remote-climate",
                ("remote", new PluginValue(Text: "koenic-ac")), ("mode", new PluginValue(Text: "heat")));
            Assert.Equal(PluginActionOutcome.Rejected, noHeat.Outcome);

            var notAnAc = await InvokeAutomated(plugin, context, "remote-climate",
                ("remote", new PluginValue(Text: "hdmi-switch")));
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
            var result = await InvokeAutomated(plugin, context, "remote-climate",
                ("remote", new PluginValue(Text: "koenic-ac")), ("mode", new PluginValue(Text: "cool")),
                ("degrees", new PluginValue(Number: 20)), ("fan", new PluginValue(Text: "medium")),
                ("toggle-swing", new PluginValue(true)));

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
            var result = await InvokeAutomated(plugin, context, "remote-run",
                ("remote", new PluginValue(Text: "hdmi-switch")),
                ("sequence", new PluginValue(Text: "android-audio-reset")));

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
            var arguments = Arguments(plugin, "remote-run",
                ("remote", new PluginValue(Text: "hdmi-switch")),
                ("sequence", new PluginValue(Text: "android-audio-reset")));
            var running = plugin.ExecuteActionAsync(
                new PluginActionRequest(Guid.NewGuid(), "remote-run", PluginActionOrigin.SessionAutomation, arguments),
                context, cancellation.Token);
            await cancellation.CancelAsync();
            var result = await running;

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
        var context = Context(temporary.Root);
        await using IrPlugin plugin = new(_ => endpoint);
        await plugin.StartAsync(host, context, CancellationToken.None);
        await plugin.ConfigureAsync(Configuration("COM3"), context, CancellationToken.None);

        Assert.Equal(PluginActionOutcome.AppliedVerified,
            (await InvokeAutomated(plugin, context, "remote-refresh")).Outcome);

        var published = host.States.Last(state => state.Key == "remotes").Value.Text ?? "";
        Assert.Contains("hdmi-switch", published);
        Assert.Contains("android-audio-reset", published);
        Assert.Contains("climate", published);
    }
}
