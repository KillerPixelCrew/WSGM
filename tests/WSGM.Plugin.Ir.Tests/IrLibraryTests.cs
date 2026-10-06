using WSGM.Plugin.Ir.Tests.Fakes;
using WSGM.Plugin.Sdk;
using WSGM.Testing;
using Xunit;
using static WSGM.Plugin.Ir.Tests.Builders.IrActions;

namespace WSGM.Plugin.Ir.Tests;

public sealed class IrLibraryTests
{
    [Fact]
    public async Task ManagedCommandsKeepStableSceneReferencesAndCarrierProvenanceAcrossRestart()
    {
        using TemporaryDirectory temporary = new();
        var context = Context(temporary.Root);
        FakeEndpoint endpoint = new();
        await using (IrPlugin plugin = new(_ => endpoint))
        {
            await plugin.StartAsync(new RecordingPluginHost(), context, CancellationToken.None);
            await plugin.ConfigureAsync(Configuration("COM3"), context, CancellationToken.None);
            Assert.Equal(PluginActionOutcome.AppliedVerified, (await Invoke(plugin, context, "connect")).Outcome);
            await Invoke(plugin, context, "learn", ("device", new PluginValue(Text: "HDMI switch")),
                ("name", new PluginValue(Text: "PC")));
            await Invoke(plugin, context, "save-scene", ("name", new PluginValue(Text: "Game")),
                ("commands", new PluginValue(Text: "HDMI switch / PC")));
            Assert.Equal(PluginActionOutcome.Rejected, (await Invoke(plugin, context, "delete")).Outcome);
            await Invoke(plugin, context, "set-carrier", ("carrier-hz", new PluginValue(Number: 40000)));
            await Invoke(plugin, context, "rename", ("device", new PluginValue(Text: "HDMI switch")),
                ("name", new PluginValue(Text: "Gaming PC")));
            Assert.Equal(PluginActionOutcome.Dispatched,
                (await Invoke(plugin, context, "scene", ("scene", new PluginValue(Text: "Game")))).Outcome);
            Assert.Equal(40000, endpoint.Sent!.CarrierHz);
            Assert.Equal("manual", endpoint.Sent.CarrierSource);
        }

        var saved = await IrLibrary.LoadAsync(Path.Combine(temporary.Root, "library.json"), CancellationToken.None);
        Assert.Equal("measured", saved.Commands[0].Payload.CarrierSource);
        Assert.Equal(36000, saved.Commands[0].Payload.CarrierHz);
        Assert.Equal(saved.Commands[0].Id, saved.Scenes[0].Steps[0].CommandId);
        await using IrPlugin restarted = new(_ => endpoint);
        await restarted.StartAsync(new RecordingPluginHost(), context with { Generation = 2 }, CancellationToken.None);
        await Invoke(restarted, context with { Generation = 2 }, "reset-carrier");
        saved = await IrLibrary.LoadAsync(Path.Combine(temporary.Root, "library.json"), CancellationToken.None);
        Assert.Null(saved.Commands[0].CarrierOverrideHz);
        Assert.Equal(36000, saved.Commands[0].TransmitPayload.CarrierHz);
    }

    [Fact]
    public async Task LearnAndSendIdentifyTheEndpointOnDemandAndAfterALostLink()
    {
        using TemporaryDirectory temporary = new();
        var context = Context(temporary.Root);
        FakeEndpoint endpoint = new();
        List<IrEndpointTarget> targets = [];
        await using IrPlugin plugin = new(target =>
        {
            targets.Add(target);
            return endpoint;
        });
        await plugin.StartAsync(new RecordingPluginHost(), context, CancellationToken.None);
        var unconfigured = await Invoke(plugin, context, "connect");
        Assert.Equal(PluginActionOutcome.Rejected, unconfigured.Outcome);
        Assert.Contains("USB serial port", unconfigured.Detail);
        await plugin.ConfigureAsync(Configuration("COM3"), context, CancellationToken.None);
        Assert.Equal(PluginActionOutcome.AppliedVerified,
            (await Invoke(plugin, context, "learn", ("device", new PluginValue(Text: "Remote")),
                ("name", new PluginValue(Text: "Power")))).Outcome);
        Assert.Equal(1, endpoint.Identifications);
        Assert.Equal([new IrEndpointTarget(false, "COM3", null)], targets);
        Assert.Equal(PluginActionOutcome.Dispatched, (await Invoke(plugin, context, "send")).Outcome);
        Assert.Equal(1, endpoint.Identifications);
        endpoint.Identity = null; // A failed exchange forgets the identity; the next send re-verifies it.
        Assert.Equal(PluginActionOutcome.Dispatched, (await Invoke(plugin, context, "send")).Outcome);
        Assert.Equal(2, endpoint.Identifications);
    }

    [Fact]
    public async Task WifiPairingOverUsbMintsATokenAndTheNetworkTransportUsesIt()
    {
        using TemporaryDirectory temporary = new();
        var context = Context(temporary.Root);
        FakeEndpoint endpoint = new();
        List<IrEndpointTarget> targets = [];
        await using (IrPlugin plugin = new(target =>
                     {
                         targets.Add(target);
                         return endpoint;
                     }))
        {
            await plugin.StartAsync(new RecordingPluginHost(), context, CancellationToken.None);
            await plugin.ConfigureAsync(Configuration("COM3", "wifi"), context, CancellationToken.None);
            var unpaired = await Invoke(plugin, context, "connect");
            Assert.Equal(PluginActionOutcome.Rejected, unpaired.Outcome);
            Assert.Contains("Pair the endpoint over USB", unpaired.Detail);
            var paired = await Invoke(plugin, context, "wifi-setup", ("ssid", new PluginValue(Text: "Home")),
                ("password", new PluginValue(Text: "hunter22")));
            Assert.Equal(PluginActionOutcome.AppliedVerified, paired.Outcome);
            Assert.Equal("Home", endpoint.Network!.Value.Ssid);
            Assert.Equal("hunter22", endpoint.Network.Value.Password);
            Assert.Equal(48, endpoint.Network.Value.Token.Length);
            Assert.Equal(new IrEndpointTarget(false, "COM3", null),
                targets.Single()); // Pairing always goes over the cable.
            await Invoke(plugin, context, "learn", ("device", new PluginValue(Text: "TV")),
                ("name", new PluginValue(Text: "Power")));
            Assert.Equal(new IrEndpointTarget(true, "wsgm-ir-abc123.local", endpoint.Network.Value.Token),
                targets.Last());
        }

        var saved = (await IrPairing.LoadAsync(Path.Combine(temporary.Root, "endpoint.json"), CancellationToken.None))!;
        Assert.Equal(endpoint.Network!.Value.Token, saved.Token);
        Assert.Equal("192.0.2.7", saved.Ip);
        Assert.DoesNotContain("hunter22", await File.ReadAllTextAsync(Path.Combine(temporary.Root, "endpoint.json")));
        await using IrPlugin restarted = new(target =>
        {
            targets.Add(target);
            return endpoint;
        });
        await restarted.StartAsync(new RecordingPluginHost(), context, CancellationToken.None);
        await restarted.ConfigureAsync(Configuration("COM3", "wifi", "10.0.0.9:7521"), context, CancellationToken.None);
        Assert.Equal(PluginActionOutcome.Dispatched, (await Invoke(restarted, context, "send")).Outcome);
        Assert.Equal(new IrEndpointTarget(true, "10.0.0.9:7521", saved.Token), targets.Last());
        Assert.Equal(PluginActionOutcome.AppliedVerified, (await Invoke(restarted, context, "wifi-clear")).Outcome);
        Assert.Equal("", endpoint.Network!.Value.Ssid);
        Assert.False(File.Exists(Path.Combine(temporary.Root, "endpoint.json")));
        Assert.Contains("Pair the endpoint over USB", (await Invoke(restarted, context, "send")).Detail);
    }

    [Fact]
    public void CarrierOverridePreservesCapturedFrequencyAndProvenance()
    {
        IrCommand captured = new("power", "TV", "Power", new IrPayload(36000, [9000, 4500], "measured"));
        var edited = captured with { CarrierOverrideHz = 38000 };
        Assert.Equal(36000, edited.Payload.CarrierHz);
        Assert.Equal("measured", edited.Payload.CarrierSource);
        Assert.Equal(38000, edited.TransmitPayload.CarrierHz);
        Assert.Equal("manual", edited.TransmitPayload.CarrierSource);
        Assert.Equal(captured.Payload, (edited with { CarrierOverrideHz = null }).TransmitPayload);
        Assert.Throws<InvalidDataException>(() => new IrPayload(38000, [9000, 4500], "detected-maybe").Validate());
    }

    [Fact]
    public void LibraryNamesRejectBidirectionalFormatting()
    {
        IrLibrary library = new(1,
            [new IrCommand("power", "TV‮device", "Power", new IrPayload(38000, [9000, 4500]))], []);

        Assert.Throws<InvalidDataException>(library.Validate);
    }

    [Fact]
    public async Task LibrarySurvivesReloadAndInvalidReplacementPreservesPreviousFile()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("library.json");
        IrLibrary library = new(1,
            [new IrCommand("pc", "HDMI switch", "PC", new IrPayload(38000, [9000, 4500, 560, 560]))],
            [new IrScene("game", "Game Mode", [new IrSceneStep("pc", 100)])]);
        await library.SaveAsync(path, CancellationToken.None);
        var loaded = await IrLibrary.LoadAsync(path, CancellationToken.None);
        Assert.Equal(library.Commands[0].Payload.TimingsUs, loaded.Commands[0].Payload.TimingsUs);
        Assert.Equal("pc", loaded.Scenes[0].Steps[0].CommandId);
        var before = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            (library with { Commands = [] }).SaveAsync(path, CancellationToken.None));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        await File.WriteAllTextAsync(path, "broken");
        await Assert.ThrowsAsync<InvalidDataException>(() => IrLibrary.LoadAsync(path, CancellationToken.None));
        Assert.Equal("broken", await File.ReadAllTextAsync(path));
        // A file that exists but cannot be opened is not an absent library.
        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => IrLibrary.LoadAsync(path, CancellationToken.None));
        }

        Assert.Equal("broken", await File.ReadAllTextAsync(path));
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
        using TemporaryDirectory temporary = new();
        await using IrPlugin plugin = new();
        Assert.True(PluginConfigurationRules.IsValid(plugin.Settings));
        Assert.All(plugin.Actions, action => Assert.True(PluginConfigurationRules.IsValid(action.Arguments)));
        var context = Context(temporary.Root);
        Assert.Equal(PluginHealth.Ready,
            await plugin.StartAsync(new RecordingPluginHost(), context, CancellationToken.None));
        var result = await plugin.ExecuteActionAsync(new PluginActionRequest(Guid.NewGuid(), "discover",
            PluginActionOrigin.User,
            new Dictionary<string, PluginValue>()), context with { Generation = 2 }, CancellationToken.None);
        Assert.Equal(PluginActionOutcome.Rejected, result.Outcome);
        Assert.True(await plugin.StopAsync(context, CancellationToken.None));
    }

    [Theory]
    [InlineData("library.json")]
    [InlineData("endpoint.json")]
    public async Task UnreadableOrCorruptStatePreventsStartupAndKeepsTheOriginalFile(string filename)
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath(filename);
        var context = Context(temporary.Root);
        await File.WriteAllTextAsync(path, "original unreadable state");
        var original = await File.ReadAllBytesAsync(path);
        FakeEndpoint endpoint = new();
        await using IrPlugin plugin = new(_ => endpoint);
        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() =>
                plugin.StartAsync(new RecordingPluginHost(), context, CancellationToken.None).AsTask());
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            plugin.StartAsync(new RecordingPluginHost(), context, CancellationToken.None).AsTask());
        Assert.Equal(PluginActionOutcome.Rejected, (await Invoke(plugin, context, "learn")).Outcome);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(0, endpoint.Identifications);
    }

    [Theory]
    [InlineData("library.json")]
    [InlineData("endpoint.json")]
    public async Task ADirectoryAtTheStatePathIsNotAnAbsentFile(string filename)
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath(filename);
        Directory.CreateDirectory(path);
        var context = Context(temporary.Root);
        await using IrPlugin plugin = new(_ => new FakeEndpoint());

        await Assert.ThrowsAsync<IOException>(() =>
            plugin.StartAsync(new RecordingPluginHost(), context, CancellationToken.None).AsTask());

        Assert.True(Directory.Exists(path));
        Assert.Equal(PluginActionOutcome.Rejected, (await Invoke(plugin, context, "learn")).Outcome);
    }

    [Fact]
    public async Task MissingStateLoadsEmptyAndUnpaired()
    {
        using TemporaryDirectory temporary = new();
        var library = await IrLibrary.LoadAsync(temporary.GetPath("missing/library.json"), CancellationToken.None);
        Assert.Empty(library.Commands);
        Assert.Empty(library.Scenes);
        Assert.Null(await IrPairing.LoadAsync(temporary.GetPath("missing/endpoint.json"), CancellationToken.None));
    }

    [Fact]
    public async Task LaterSceneRefusalKeepsTheEarlierEmissionUnconfirmed()
    {
        using TemporaryDirectory temporary = new();
        var context = Context(temporary.Root);
        IrLibrary library = new(1, [
            new IrCommand("power", "TV", "Power", new IrPayload(38000, [9000, 4500]))
        ], [new IrScene("twice", "Twice", [new IrSceneStep("power"), new IrSceneStep("power")])]);
        await library.SaveAsync(temporary.GetPath("library.json"), CancellationToken.None);
        FakeEndpoint endpoint = new();
        endpoint.Transmitted = () => endpoint.TransmitFailure = new IrRejectedException("busy");
        await using IrPlugin plugin = new(_ => endpoint);
        await plugin.StartAsync(new RecordingPluginHost(), context, CancellationToken.None);
        await plugin.ConfigureAsync(Configuration("COM3"), context, CancellationToken.None);

        var result = await InvokeAutomated(plugin, context, "scene", ("scene", new PluginValue(Text: "twice")));

        Assert.Equal(PluginActionOutcome.Unconfirmed, result.Outcome);
        Assert.Equal(2, endpoint.Transmissions);
        Assert.NotNull(endpoint.Sent);
    }

    [Fact]
    public async Task PublicationRefusalAfterSendCannotEraseItsEmission()
    {
        using TemporaryDirectory temporary = new();
        var context = Context(temporary.Root);
        IrLibrary library = new(1, [
            new IrCommand("power", "TV", "Power", new IrPayload(38000, [9000, 4500]))
        ], []);
        await library.SaveAsync(temporary.GetPath("library.json"), CancellationToken.None);
        FakeEndpoint endpoint = new();
        RecordingPluginHost host = new();
        await using IrPlugin plugin = new(_ => endpoint);
        await plugin.StartAsync(host, context, CancellationToken.None);
        await plugin.ConfigureAsync(Configuration("COM3"), context, CancellationToken.None);
        host.Publishing = publication =>
        {
            if (publication.Value.Text == "IR emitted; appliance state is not verified.")
            {
                throw new IrRejectedException("publication refused");
            }
        };

        var result = await InvokeAutomated(plugin, context, "send");

        Assert.Equal(PluginActionOutcome.Unconfirmed, result.Outcome);
        Assert.Equal(1, endpoint.Transmissions);
    }

    [Fact]
    public async Task DeletingTheSelectedCommandDoesNotSilentlySelectAndSendAnotherOne()
    {
        using TemporaryDirectory temporary = new();
        var context = Context(temporary.Root);
        IrLibrary library = new(1, [
            new IrCommand("power", "TV", "Power", new IrPayload(38000, [9000, 4500])),
            new IrCommand("input", "TV", "Input", new IrPayload(38000, [560, 560]))
        ], [], "power");
        var path = temporary.GetPath("library.json");
        await library.SaveAsync(path, CancellationToken.None);
        FakeEndpoint endpoint = new();
        RecordingPluginHost host = new();
        await using IrPlugin plugin = new(_ => endpoint);
        await plugin.StartAsync(host, context, CancellationToken.None);
        await plugin.ConfigureAsync(Configuration("COM3"), context, CancellationToken.None);

        Assert.Equal(PluginActionOutcome.AppliedVerified, (await Invoke(plugin, context, "delete")).Outcome);
        Assert.Equal(PluginActionOutcome.Rejected, (await InvokeAutomated(plugin, context, "send")).Outcome);
        Assert.Equal("No command selected", host.States.Last(state => state.Key == "selected").Value.Text);
        Assert.Null((await IrLibrary.LoadAsync(path, CancellationToken.None)).SelectedCommandId);
        Assert.Equal(0, endpoint.Transmissions);
        Assert.Equal(0, endpoint.Identifications);
    }

    [Fact]
    public async Task LibraryPublicationKeepsItsKeysAndSelectedCommandText()
    {
        using TemporaryDirectory temporary = new();
        var context = Context(temporary.Root);
        RecordingPluginHost host = new();
        await using IrPlugin plugin = new(_ => new FakeEndpoint());
        await plugin.StartAsync(host, context, CancellationToken.None);
        Assert.Equal(["status", "network", "library", "selected", "carrier-source", "remotes"],
            host.States.Select(state => state.Key));
        Assert.Equal("No command selected", host.States.Single(state => state.Key == "selected").Value.Text);
        await plugin.ConfigureAsync(Configuration("COM3"), context, CancellationToken.None);

        await Invoke(plugin, context, "learn", ("device", new PluginValue(Text: "TV")),
            ("name", new PluginValue(Text: "Power")));
        await Invoke(plugin, context, "learn", ("device", new PluginValue(Text: "TV")),
            ("name", new PluginValue(Text: "Input")));
        await Invoke(plugin, context, "select", ("command", new PluginValue(Text: "TV / Power")));

        Assert.Equal("TV / Power; repeats 0, gap 40 ms",
            host.States.Last(state => state.Key == "selected").Value.Text);
        Assert.Equal("2 commands, 0 scenes\nTV / Power\nTV / Input\nScenes: ",
            host.States.Last(state => state.Key == "library").Value.Text);
        var carrier = host.States.Last(state => state.Key == "carrier-hz");
        Assert.Equal(36000, carrier.Value.Number);
        Assert.Equal(PluginStateOrigin.Action, carrier.Origin);
        Assert.NotNull(carrier.OperationId);
    }
}
