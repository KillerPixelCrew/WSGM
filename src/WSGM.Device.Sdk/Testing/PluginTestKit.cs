using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Input;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Sdk.Testing;

/// <summary>One simulator-only capability fixture case.</summary>
public sealed record CapabilityFixtureCase
{
    /// <summary>Stable human-readable case name.</summary>
    public required string Name { get; init; }

    /// <summary>Semantic command replayed through the SDK registry.</summary>
    public required CapabilityCommand Command { get; init; }

    /// <summary>Expected command outcome.</summary>
    public required CommandOutcome ExpectedOutcome { get; init; }

    /// <summary>Expected structured reason, when relevant.</summary>
    public CapabilityReasonCode? ExpectedReasonCode { get; init; }
}

/// <summary>Actual result and expectation match for one fixture case.</summary>
public sealed record CapabilityFixtureResult
{
    /// <summary>Fixture case name.</summary>
    public required string Name { get; init; }

    /// <summary>Whether outcome and reason matched.</summary>
    public required bool Matched { get; init; }

    /// <summary>Normalized registry result.</summary>
    public required CapabilityCommandResult Actual { get; init; }
}

/// <summary>Replays semantic command fixtures without opening hardware.</summary>
public static class CapabilityFixtureRunner
{
    /// <summary>Runs cases in declared order through one registry.</summary>
    /// <param name="registry">Simulator-backed capability registry.</param>
    /// <param name="cases">Reviewable fixture cases.</param>
    /// <param name="cancellationToken">Cancels replay.</param>
    /// <returns>One comparison result per input case.</returns>
    public static async ValueTask<IReadOnlyList<CapabilityFixtureResult>> RunAsync(
        CapabilityRegistry registry,
        IReadOnlyList<CapabilityFixtureCase> cases,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(cases);

        var results = new List<CapabilityFixtureResult>(cases.Count);
        foreach (CapabilityFixtureCase fixtureCase in cases)
        {
            ArgumentNullException.ThrowIfNull(fixtureCase);
            cancellationToken.ThrowIfCancellationRequested();
            CapabilityCommandResult actual = await registry.ExecuteAsync(
                fixtureCase.Command,
                cancellationToken).ConfigureAwait(false);
            results.Add(new CapabilityFixtureResult
            {
                Name = fixtureCase.Name,
                Matched = actual.Outcome == fixtureCase.ExpectedOutcome
                    && actual.Reason?.Code == fixtureCase.ExpectedReasonCode,
                Actual = actual,
            });
        }

        return results;
    }
}

/// <summary>In-memory semantic DeviceHost adapter for fixture and plugin tests.</summary>
/// <remarks>No member exposes or simulates a raw hardware transport.</remarks>
public sealed class TestPluginHostAdapter : IPluginHostAdapter
{
    private readonly object _gate = new();
    private readonly List<CapabilityDescriptorSet> _descriptorSets = [];
    private readonly List<CapabilityState> _capabilityStates = [];
    private readonly List<PluginResourceState> _resourceStates = [];
    private readonly List<IReadOnlyList<PhysicalDeviceIdentity>> _physicalDeviceSets = [];
    private readonly List<CanonicalControllerSample> _controllerSamples = [];
    private readonly List<IReadOnlyList<OemControlDescriptor>> _oemControlSets = [];
    private readonly List<OemControlEvent> _oemEvents = [];

    /// <summary>Creates a host simulator for fixed generations.</summary>
    /// <param name="hostGeneration">Host generation exposed to the plugin.</param>
    /// <param name="deviceGeneration">Device generation exposed to the plugin.</param>
    public TestPluginHostAdapter(long hostGeneration, long deviceGeneration)
    {
        if (hostGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hostGeneration));
        }

        if (deviceGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceGeneration));
        }

        HostGeneration = hostGeneration;
        DeviceGeneration = deviceGeneration;
    }

    /// <inheritdoc />
    public long HostGeneration { get; }

    /// <inheritdoc />
    public long DeviceGeneration { get; }

    /// <summary>Descriptor replacements in publication order.</summary>
    public IReadOnlyList<CapabilityDescriptorSet> DescriptorSets => Snapshot(_descriptorSets);

    /// <summary>Capability observations in publication order.</summary>
    public IReadOnlyList<CapabilityState> CapabilityStates => Snapshot(_capabilityStates);

    /// <summary>Resource observations in publication order.</summary>
    public IReadOnlyList<PluginResourceState> ResourceStates => Snapshot(_resourceStates);

    /// <summary>Physical-device sets in publication order.</summary>
    public IReadOnlyList<IReadOnlyList<PhysicalDeviceIdentity>> PhysicalDeviceSets =>
        Snapshot(_physicalDeviceSets);

    /// <summary>Canonical controller samples in publication order.</summary>
    public IReadOnlyList<CanonicalControllerSample> ControllerSamples => Snapshot(_controllerSamples);

    /// <summary>OEM-control descriptor sets in publication order.</summary>
    public IReadOnlyList<IReadOnlyList<OemControlDescriptor>> OemControlSets => Snapshot(_oemControlSets);

    /// <summary>OEM-control events in publication order.</summary>
    public IReadOnlyList<OemControlEvent> OemEvents => Snapshot(_oemEvents);

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
    public ValueTask PublishResourceStateAsync(
        PluginResourceState state,
        CancellationToken cancellationToken) =>
        RecordAsync(_resourceStates, state, cancellationToken);

    /// <inheritdoc />
    public ValueTask PublishPhysicalDevicesAsync(
        IReadOnlyList<PhysicalDeviceIdentity> devices,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(devices);
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
