using WSGM.Plugin.Sdk;
using Xunit;

namespace WSGM.Plugin.Ir.Tests;

public sealed class IrLibraryTests
{
    [Fact]
    public async Task ManagedCommandsKeepStableSceneReferencesAndCarrierProvenanceAcrossRestart()
    {
        string folder = Path.Combine(Path.GetTempPath(), "wsgm-ir-tests-" + Guid.NewGuid().ToString("N"));
        PluginContext context = new(new("wsgm.ir", "test"), 1, PluginSessionMode.Desktop,
            DateTimeOffset.UtcNow.AddMinutes(1), folder);
        FakeEndpoint endpoint = new();
        try
        {
            await using (IrPlugin plugin = new(_ => endpoint))
            {
                await plugin.StartAsync(new Host(), context, default);
                await plugin.ConfigureAsync(Configuration(port: "COM3"), context, default);
                Assert.Equal(PluginActionOutcome.AppliedVerified, (await Invoke(plugin, context, "connect")).Outcome);
                await Invoke(plugin, context, "learn", ("device", new(Text: "HDMI switch")), ("name", new(Text: "PC")));
                await Invoke(plugin, context, "save-scene", ("name", new(Text: "Game")),
                    ("commands", new(Text: "HDMI switch / PC")));
                Assert.Equal(PluginActionOutcome.Rejected, (await Invoke(plugin, context, "delete")).Outcome);
                await Invoke(plugin, context, "set-carrier", ("carrier-hz", new(Number: 40000)));
                await Invoke(plugin, context, "rename", ("device", new(Text: "HDMI switch")), ("name", new(Text: "Gaming PC")));
                Assert.Equal(PluginActionOutcome.Dispatched, (await Invoke(plugin, context, "scene", ("scene", new(Text: "Game")))).Outcome);
                Assert.Equal(40000, endpoint.Sent!.CarrierHz);
                Assert.Equal("manual", endpoint.Sent.CarrierSource);
            }
            IrLibrary saved = await IrLibrary.LoadAsync(Path.Combine(folder, "library.json"), default);
            Assert.Equal("measured", saved.Commands[0].Payload.CarrierSource);
            Assert.Equal(36000, saved.Commands[0].Payload.CarrierHz);
            Assert.Equal(saved.Commands[0].Id, saved.Scenes[0].Steps[0].CommandId);
            await using IrPlugin restarted = new(_ => endpoint);
            await restarted.StartAsync(new Host(), context with { Generation = 2 }, default);
            await Invoke(restarted, context with { Generation = 2 }, "reset-carrier");
            saved = await IrLibrary.LoadAsync(Path.Combine(folder, "library.json"), default);
            Assert.Null(saved.Commands[0].CarrierOverrideHz);
            Assert.Equal(36000, saved.Commands[0].TransmitPayload.CarrierHz);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    private static ValueTask<PluginActionResult> Invoke(IrPlugin plugin, PluginContext context, string action,
        params (string Key, PluginValue Value)[] changes)
    {
        Dictionary<string, PluginValue> arguments = plugin.Actions.Single(item => item.Id == action)
            .Arguments.ToDictionary(item => item.Key, item => item.Default);
        foreach (var change in changes) { arguments[change.Key] = change.Value; }
        return plugin.ExecuteActionAsync(new(Guid.NewGuid(), action, PluginActionOrigin.User, arguments), context, default);
    }

    [Fact]
    public async Task LearnAndSendIdentifyTheEndpointOnDemandAndAfterALostLink()
    {
        string folder = Path.Combine(Path.GetTempPath(), "wsgm-ir-tests-" + Guid.NewGuid().ToString("N"));
        PluginContext context = new(new("wsgm.ir", "test"), 1, PluginSessionMode.Desktop, DateTimeOffset.UtcNow.AddMinutes(1), folder);
        FakeEndpoint endpoint = new();
        List<IrEndpointTarget> targets = [];
        try
        {
            await using IrPlugin plugin = new(target => { targets.Add(target); return endpoint; });
            await plugin.StartAsync(new Host(), context, default);
            PluginActionResult unconfigured = await Invoke(plugin, context, "connect");
            Assert.Equal(PluginActionOutcome.Unconfirmed, unconfigured.Outcome);
            Assert.Contains("USB serial port", unconfigured.Detail);
            await plugin.ConfigureAsync(Configuration(port: "COM3"), context, default);
            Assert.Equal(PluginActionOutcome.AppliedVerified,
                (await Invoke(plugin, context, "learn", ("device", new(Text: "Remote")), ("name", new(Text: "Power")))).Outcome);
            Assert.Equal(1, endpoint.Identifications);
            Assert.Equal([new IrEndpointTarget(false, "COM3", null)], targets);
            Assert.Equal(PluginActionOutcome.Dispatched, (await Invoke(plugin, context, "send")).Outcome);
            Assert.Equal(1, endpoint.Identifications);
            endpoint.Identity = null; // A failed exchange forgets the identity; the next send re-verifies it.
            Assert.Equal(PluginActionOutcome.Dispatched, (await Invoke(plugin, context, "send")).Outcome);
            Assert.Equal(2, endpoint.Identifications);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    [Fact]
    public async Task WifiPairingOverUsbMintsATokenAndTheNetworkTransportUsesIt()
    {
        string folder = Path.Combine(Path.GetTempPath(), "wsgm-ir-tests-" + Guid.NewGuid().ToString("N"));
        PluginContext context = new(new("wsgm.ir", "test"), 1, PluginSessionMode.Desktop, DateTimeOffset.UtcNow.AddMinutes(1), folder);
        FakeEndpoint endpoint = new();
        List<IrEndpointTarget> targets = [];
        try
        {
            await using (IrPlugin plugin = new(target => { targets.Add(target); return endpoint; }))
            {
                await plugin.StartAsync(new Host(), context, default);
                await plugin.ConfigureAsync(Configuration(port: "COM3", transport: "wifi"), context, default);
                PluginActionResult unpaired = await Invoke(plugin, context, "connect");
                Assert.Equal(PluginActionOutcome.Unconfirmed, unpaired.Outcome);
                Assert.Contains("Pair the endpoint over USB", unpaired.Detail);
                PluginActionResult paired = await Invoke(plugin, context, "wifi-setup", ("ssid", new(Text: "Home")), ("password", new(Text: "hunter22")));
                Assert.Equal(PluginActionOutcome.AppliedVerified, paired.Outcome);
                Assert.Equal("Home", endpoint.Network!.Value.Ssid);
                Assert.Equal("hunter22", endpoint.Network.Value.Password);
                Assert.Equal(48, endpoint.Network.Value.Token.Length);
                Assert.Equal(new IrEndpointTarget(false, "COM3", null), targets.Single()); // Pairing always goes over the cable.
                await Invoke(plugin, context, "learn", ("device", new(Text: "TV")), ("name", new(Text: "Power")));
                Assert.Equal(new IrEndpointTarget(true, "wsgm-ir-abc123.local", endpoint.Network.Value.Token), targets.Last());
            }
            IrPairing saved = (await IrPairing.LoadAsync(Path.Combine(folder, "endpoint.json"), default))!;
            Assert.Equal(endpoint.Network!.Value.Token, saved.Token);
            Assert.Equal("192.0.2.7", saved.Ip);
            Assert.DoesNotContain("hunter22", await File.ReadAllTextAsync(Path.Combine(folder, "endpoint.json")));
            await using IrPlugin restarted = new(target => { targets.Add(target); return endpoint; });
            await restarted.StartAsync(new Host(), context, default);
            await restarted.ConfigureAsync(Configuration(port: "COM3", transport: "wifi", host: "10.0.0.9:7521"), context, default);
            Assert.Equal(PluginActionOutcome.Dispatched, (await Invoke(restarted, context, "send")).Outcome);
            Assert.Equal(new IrEndpointTarget(true, "10.0.0.9:7521", saved.Token), targets.Last());
            Assert.Equal(PluginActionOutcome.AppliedVerified, (await Invoke(restarted, context, "wifi-clear")).Outcome);
            Assert.Equal("", endpoint.Network!.Value.Ssid);
            Assert.False(File.Exists(Path.Combine(folder, "endpoint.json")));
            Assert.Contains("Pair the endpoint over USB", (await Invoke(restarted, context, "send")).Detail);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    private static PluginConfiguration Configuration(string port = "", string transport = "usb", string host = "") =>
        new(1, PluginConfigurationOrigin.User, new Dictionary<string, PluginValue>
        {
            ["port"] = new(Text: port),
            ["transport"] = new(Text: transport),
            ["host"] = new(Text: host),
        });

    [Fact]
    public void CarrierOverridePreservesCapturedFrequencyAndProvenance()
    {
        IrCommand captured = new("power", "TV", "Power", new(36000, [9000, 4500], "measured"));
        IrCommand edited = captured with { CarrierOverrideHz = 38000 };
        Assert.Equal(36000, edited.Payload.CarrierHz);
        Assert.Equal("measured", edited.Payload.CarrierSource);
        Assert.Equal(38000, edited.TransmitPayload.CarrierHz);
        Assert.Equal("manual", edited.TransmitPayload.CarrierSource);
        Assert.Equal(captured.Payload, (edited with { CarrierOverrideHz = null }).TransmitPayload);
        Assert.Throws<InvalidDataException>(() => new IrPayload(38000, [9000, 4500], "detected-maybe").Validate());
    }

    [Fact]
    public async Task LibrarySurvivesReloadAndInvalidReplacementPreservesPreviousFile()
    {
        string folder = Path.Combine(Path.GetTempPath(), "wsgm-ir-tests-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "library.json");
        try
        {
            IrLibrary library = new(1, [new("pc", "HDMI switch", "PC", new(38000, [9000, 4500, 560, 560]))],
                [new("game", "Game Mode", [new("pc", 100)])]);
            await library.SaveAsync(path, default);
            IrLibrary loaded = await IrLibrary.LoadAsync(path, default);
            Assert.Equal(library.Commands[0].Payload.TimingsUs, loaded.Commands[0].Payload.TimingsUs);
            Assert.Equal("pc", loaded.Scenes[0].Steps[0].CommandId);
            byte[] before = await File.ReadAllBytesAsync(path);
            await Assert.ThrowsAsync<InvalidDataException>(() => (library with { Commands = [] }).SaveAsync(path, default));
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
            await File.WriteAllTextAsync(path, "broken");
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => IrLibrary.LoadAsync(path, default));
            Assert.Equal("broken", await File.ReadAllTextAsync(path));
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    [Theory]
    [InlineData(19000, 560, 0)]
    [InlineData(38000, 0, 0)]
    [InlineData(38000, 65536, 0)]
    [InlineData(38000, 560, 5)]
    public void UnsafePayloadCannotReachEndpoint(int carrier, int timing, int repeats)
    {
        Assert.Throws<InvalidDataException>(() => new IrPayload(carrier, [timing, timing]).Validate(repeats));
    }

    [Fact]
    public async Task IndependentPluginHasValidDeclarationsAndRejectsStaleGeneration()
    {
        await using IrPlugin plugin = new();
        Assert.True(PluginConfigurationRules.IsValid(plugin.Settings));
        Assert.All(plugin.Actions, action => Assert.True(PluginConfigurationRules.IsValid(action.Arguments)));
        PluginContext context = new(new("wsgm.ir", "test"), 1, PluginSessionMode.Desktop,
            DateTimeOffset.UtcNow.AddSeconds(10), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.Equal(PluginHealth.Ready, await plugin.StartAsync(new Host(), context, default));
        PluginActionResult result = await plugin.ExecuteActionAsync(new(Guid.NewGuid(), "discover", PluginActionOrigin.User,
            new Dictionary<string, PluginValue>()), context with { Generation = 2 }, default);
        Assert.Equal(PluginActionOutcome.Rejected, result.Outcome);
        Assert.True(await plugin.StopAsync(context, default));
    }

    private sealed class Host : IPluginHost
    {
        public void PublishHealth(PluginHealthPublication publication) { }
    }
}
