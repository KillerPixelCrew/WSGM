using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Device.Sdk.Testing;

/// <summary>In-memory host adapter for focused plugin tests.</summary>
/// <remarks>The adapter records only the semantic publications available to production plugins.</remarks>
public sealed class TestPluginHostAdapter : IPluginHostAdapter
{
    private readonly object _gate = new();
    private readonly List<CapabilityDescriptorSet> _descriptorSets = [];
    private readonly List<CapabilityState> _capabilityStates = [];
    private readonly List<IReadOnlyList<PhysicalDeviceIdentity>> _physicalDeviceSets = [];
    private readonly List<CanonicalControllerSample> _controllerSamples = [];
    private readonly List<IReadOnlyList<OemControlDescriptor>> _oemControlSets = [];
    private readonly List<OemControlEvent> _oemEvents = [];
    private readonly List<PluginSettingsManifest> _settingsManifests = [];
    private readonly List<(DeviceTraceLevel Level, string Scope, string Message)> _traces = [];

    /// <summary>Creates an adapter for one cycle generation.</summary>
    /// <param name="cycleGeneration">Cycle generation exposed to the plugin.</param>
    public TestPluginHostAdapter(long cycleGeneration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cycleGeneration);
        CycleGeneration = cycleGeneration;
    }

    /// <inheritdoc />
    public long CycleGeneration { get; }

    /// <summary>Descriptor replacements in publication order.</summary>
    public IReadOnlyList<CapabilityDescriptorSet> DescriptorSets => Snapshot(_descriptorSets);

    /// <summary>Capability observations in publication order.</summary>
    public IReadOnlyList<CapabilityState> CapabilityStates => Snapshot(_capabilityStates);

    /// <summary>Physical-device sets in publication order.</summary>
    public IReadOnlyList<IReadOnlyList<PhysicalDeviceIdentity>> PhysicalDeviceSets =>
        Snapshot(_physicalDeviceSets);

    /// <summary>Haptic capabilities from the most recent physical-device publication.</summary>
    public HapticCapabilities? PublishedOutput { get; private set; }

    /// <summary>Canonical controller samples in publication order.</summary>
    public IReadOnlyList<CanonicalControllerSample> ControllerSamples => Snapshot(_controllerSamples);

    /// <summary>OEM-control descriptor sets in publication order.</summary>
    public IReadOnlyList<IReadOnlyList<OemControlDescriptor>> OemControlSets => Snapshot(_oemControlSets);

    /// <summary>OEM-control events in publication order.</summary>
    public IReadOnlyList<OemControlEvent> OemEvents => Snapshot(_oemEvents);

    /// <summary>Settings manifests the plugin declared, in publication order.</summary>
    public IReadOnlyList<PluginSettingsManifest> SettingsManifests => Snapshot(_settingsManifests);

    /// <summary>Trace lines in emission order.</summary>
    /// <remarks>
    /// Recorded rather than discarded so a plugin's diagnostics are testable like anything else it
    /// publishes. A test that asserts a decision was traced is what keeps instrumentation from
    /// being quietly deleted later.
    /// </remarks>
    public IReadOnlyList<(DeviceTraceLevel Level, string Scope, string Message)> Traces =>
        Snapshot(_traces);

    /// <inheritdoc />
    public ValueTask PublishDescriptorsAsync(
        CapabilityDescriptorSet descriptors,
        CancellationToken cancellationToken) =>
        RecordAsync(_descriptorSets, descriptors, cancellationToken);

    /// <inheritdoc />
    public ValueTask PublishCapabilityStateAsync(
        CapabilityState state,
        CancellationToken cancellationToken) =>
        RecordAsync(_capabilityStates, state, cancellationToken);

    /// <inheritdoc />
    public ValueTask PublishPhysicalDevicesAsync(
        IReadOnlyList<PhysicalDeviceIdentity> devices,
        HapticCapabilities? output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(devices);
        PublishedOutput = output;
        return RecordAsync(
            _physicalDeviceSets,
            (IReadOnlyList<PhysicalDeviceIdentity>)[.. devices],
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask PublishControllerSampleAsync(
        CanonicalControllerSample sample,
        CancellationToken cancellationToken) =>
        RecordAsync(_controllerSamples, sample, cancellationToken);

    /// <inheritdoc />
    public ValueTask PublishOemControlsAsync(
        IReadOnlyList<OemControlDescriptor> controls,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controls);
        return RecordAsync(
            _oemControlSets,
            (IReadOnlyList<OemControlDescriptor>)[.. controls],
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask PublishOemEventAsync(
        OemControlEvent controlEvent,
        CancellationToken cancellationToken) =>
        RecordAsync(_oemEvents, controlEvent, cancellationToken);

    /// <inheritdoc />
    public ValueTask PublishSettingsManifestAsync(
        PluginSettingsManifest manifest,
        CancellationToken cancellationToken) =>
        RecordAsync(_settingsManifests, manifest, cancellationToken);

    /// <inheritdoc />
    public void Trace(DeviceTraceLevel level, string scope, string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        lock (_gate)
        {
            _traces.Add((level, scope ?? string.Empty, message));
        }
    }

    private ValueTask RecordAsync<T>(
        List<T> target,
        T item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            target.Add(item);
        }

        return ValueTask.CompletedTask;
    }

    private IReadOnlyList<T> Snapshot<T>(List<T> source)
    {
        lock (_gate)
        {
            return [.. source];
        }
    }
}
