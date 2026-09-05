using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Settings;
using WSGM.Device.Sdk.Testing;

namespace WSGM.Device.Sdk.Tests;

public sealed class PluginTraceChangeTests
{
    [Fact]
    public void ChangeReachesTheHostWithItsScopeKeyAndLevel()
    {
        TestPluginHostAdapter adapter = Record(
            () => PluginTrace.Change("motion", "freshness", "holding rest", DeviceTraceLevel.Debug));

        var line = Assert.Single(adapter.Changes);
        Assert.Equal(DeviceTraceLevel.Debug, line.Level);
        Assert.Equal("motion", line.Scope);
        Assert.Equal("freshness", line.Key);
        Assert.Equal("holding rest", line.Message);
        Assert.Empty(adapter.Traces);
    }

    [Fact]
    public void ChangeDefaultsToInfo()
    {
        TestPluginHostAdapter adapter = Record(
            () => PluginTrace.Change("motion", "freshness", "resumed"));

        Assert.Equal(DeviceTraceLevel.Info, Assert.Single(adapter.Changes).Level);
    }

    [Fact]
    public void DebugTracesAtTheSuppressedLevel()
    {
        TestPluginHostAdapter adapter = Record(() => PluginTrace.Debug("motion", "sensor age 12 ms"));

        Assert.Equal(DeviceTraceLevel.Debug, Assert.Single(adapter.Traces).Level);
    }

    [Fact]
    public void ChangeIsSilentWithNoSink()
    {
        PluginTrace.Install(null);
        PluginTrace.Change("motion", "freshness", "dropped on the floor");
    }

    [Fact]
    public void ChangeIsSilentForAnEmptyMessage()
    {
        TestPluginHostAdapter adapter = Record(
            () => PluginTrace.Change("motion", "freshness", string.Empty));

        Assert.Empty(adapter.Changes);
    }

    [Fact]
    public void ChangeTruncatesToTheDocumentedLimit()
    {
        TestPluginHostAdapter adapter = Record(() => PluginTrace.Change(
            "motion",
            "freshness",
            new string('x', PluginTrace.MaxMessageLength * 2)));

        Assert.Equal(PluginTrace.MaxMessageLength, Assert.Single(adapter.Changes).Message.Length);
    }

    [Fact]
    public void ChangeRequiresAKey()
    {
        TestPluginHostAdapter adapter = new(1);
        PluginTrace.Install(adapter);
        try
        {
            Assert.ThrowsAny<ArgumentException>(
                () => PluginTrace.Change("motion", string.Empty, "holding rest"));
        }
        finally
        {
            PluginTrace.Install(null);
        }
    }

    [Fact]
    public void AHostThatDoesNotImplementTraceChangeStillReceivesTheLine()
    {
        // The compatibility promise of API 3: a host written against API 2 declares no TraceChange,
        // so the interface default runs and the line arrives through Trace instead of being lost.
        HostPredatingTraceChange adapter = new();
        PluginTrace.Install(adapter);
        try
        {
            PluginTrace.Change("motion", "freshness", "holding rest", DeviceTraceLevel.Warn);
        }
        finally
        {
            PluginTrace.Install(null);
        }

        var line = Assert.Single(adapter.Lines);
        Assert.Equal(DeviceTraceLevel.Warn, line.Level);
        Assert.Equal("motion", line.Scope);
        Assert.Equal("holding rest", line.Message);
    }

    [Fact]
    public void AddingDebugDidNotMoveTheExistingLevelValues()
    {
        // Plugins and hosts are separately compiled binaries; a shifted enum value would silently
        // reinterpret every trace an already published package emits.
        Assert.Equal(0, (int)DeviceTraceLevel.Info);
        Assert.Equal(1, (int)DeviceTraceLevel.Warn);
        Assert.Equal(2, (int)DeviceTraceLevel.Error);
        Assert.Equal(3, (int)DeviceTraceLevel.Debug);
    }

    private static TestPluginHostAdapter Record(Action trace)
    {
        TestPluginHostAdapter adapter = new(1);
        PluginTrace.Install(adapter);
        try
        {
            trace();
        }
        finally
        {
            PluginTrace.Install(null);
        }

        return adapter;
    }

    /// <summary>An adapter implementing only what API 2 declared.</summary>
    private sealed class HostPredatingTraceChange : IPluginHostAdapter
    {
        public List<(DeviceTraceLevel Level, string Scope, string Message)> Lines { get; } = [];

        public long CycleGeneration => 1;

        public void Trace(DeviceTraceLevel level, string scope, string message) =>
            Lines.Add((level, scope, message));

        public ValueTask PublishDescriptorsAsync(
            CapabilityDescriptorSet descriptors,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask PublishCapabilityStateAsync(
            CapabilityState state,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask PublishPhysicalDevicesAsync(
            IReadOnlyList<PhysicalDeviceIdentity> devices,
            HapticCapabilities? output,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask PublishControllerSampleAsync(
            CanonicalControllerSample sample,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask PublishOemControlsAsync(
            IReadOnlyList<OemControlDescriptor> controls,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask PublishOemEventAsync(
            OemControlEvent controlEvent,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask PublishSettingsManifestAsync(
            PluginSettingsManifest manifest,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
