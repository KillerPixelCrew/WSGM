using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Settings;
using WSGM.Device.Sdk.Testing;

namespace WSGM.Device.Sdk.Tests;

/// <summary>
/// The diagnostic channel plugins write through, and the guarantees the layers above it rely on.
/// </summary>
/// <remarks>
/// These matter more than their size suggests. The channel exists because five separate device
/// faults were diagnosed by adding temporary instrumentation and rebuilding — the shipped plugin
/// could not say why it had done nothing. Instrumentation that throws, or that a plugin can use to
/// flood the log, would put the log back to being unreadable in the other direction.
/// </remarks>
public sealed class PluginTraceTests
{
    [Fact]
    public void ChangeReachesTheHostWithItsScopeKeyAndLevel()
    {
        var adapter = Record(
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
        var adapter = Record(
            () => PluginTrace.Change("motion", "freshness", "resumed"));

        Assert.Equal(DeviceTraceLevel.Info, Assert.Single(adapter.Changes).Level);
    }

    [Fact]
    public void DebugTracesAtTheSuppressedLevel()
    {
        var adapter = Record(() => PluginTrace.Debug("motion", "sensor age 12 ms"));

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
        var adapter = Record(
            () => PluginTrace.Change("motion", "freshness", string.Empty));

        Assert.Empty(adapter.Changes);
    }

    [Fact]
    public void ChangeTruncatesToTheDocumentedLimit()
    {
        var adapter = Record(() => PluginTrace.Change(
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

    [Fact]
    public void TracingWithNoSinkInstalledIsSilentRatherThanFatal()
    {
        // A plugin traces from catch blocks and from startup paths that run before anything is
        // wired. If that could throw, instrumentation would itself be a source of faults and the
        // rational response would be to remove it.
        PluginTrace.Install(null);

        PluginTrace.Info("scope", "message");
        PluginTrace.Warn("scope", "message");
        PluginTrace.Error("scope", "message");
        PluginTrace.Failure("scope", "context", new InvalidOperationException("boom"));
    }

    [Fact]
    public void FailureRecordsTheExceptionTypeAndMessageNotJustTheType()
    {
        // The DllNotFoundException that stalled a device cycle reached the log as its type name
        // alone. The message was the entire diagnosis: it named the library.
        TestPluginHostAdapter host = new(1);
        PluginTrace.Install(host);
        try
        {
            PluginTrace.Failure(
                "loader",
                "starting the plugin",
                new DllNotFoundException("ole32.dll"));
        }
        finally
        {
            PluginTrace.Install(null);
        }

        var trace = Assert.Single(host.Traces);
        Assert.Equal(DeviceTraceLevel.Warn, trace.Level);
        Assert.Equal("loader", trace.Scope);
        Assert.Contains("starting the plugin", trace.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DllNotFoundException), trace.Message, StringComparison.Ordinal);
        Assert.Contains("ole32.dll", trace.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTestAdapterRecordsTracesSoPluginDiagnosticsAreAssertable()
    {
        TestPluginHostAdapter host = new(3);
        host.Trace(DeviceTraceLevel.Info, "controller", "switching to DirectInput");
        host.Trace(DeviceTraceLevel.Warn, "wmi", "provider probe failed");

        var traces = host.Traces;
        Assert.Equal(2, traces.Count);
        Assert.Equal("controller", traces[0].Scope);
        Assert.Equal(DeviceTraceLevel.Warn, traces[1].Level);
    }

    [Fact]
    public void AnEmptyTraceIsDroppedRatherThanWrittenAsABlankLine()
    {
        TestPluginHostAdapter host = new(1);
        host.Trace(DeviceTraceLevel.Info, "scope", string.Empty);

        Assert.Empty(host.Traces);
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
