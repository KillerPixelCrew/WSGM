using WSGM.Plugin.Sdk;
using Xunit;

namespace WSGM.Plugin.Ir.Tests;

public sealed class IrLibraryTests
{
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
