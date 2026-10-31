using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using HandheldDefinition = LibHandheld.Contracts.HandheldDefinition;

namespace WSGM.Shell;

/// <summary>Applies named lighting profiles and restores desired lighting on the active device.</summary>
/// <remarks>
///     A selected profile's color is projected onto every writable zone as desired state, never copied
///     into saved configuration; this owner therefore publishes the router's desired context. It also
///     restores lighting once the zones become ready and after power transitions, within the attempt
///     budget of <see cref="DeviceLightingRestore" />, and never repeats an uncertain write.
/// </remarks>
internal sealed class DeviceLightingProfiles
{
    private readonly Func<DeviceProfileScope?> _activeProfileScope;
    private readonly Dictionary<DeviceCapabilityKey, (int Color, CommandOutcome Outcome)> _attempts = [];
    private readonly DeviceCapabilityRouter _capabilities;
    private readonly Func<bool> _closed;
    private readonly Action _configurationChanged;
    private readonly Func<HandheldDefinition?> _definition;
    private readonly Func<string?> _deviceIdentityKey;

    private readonly Func<string, string?, CapabilityValue?, CapabilityCommandOrigin, bool, CancellationToken,
        Task<CapabilityCommandResult>> _execute;

    private readonly Func<bool> _integrationEnabled;
    private readonly CancellationToken _lifetime;
    private readonly Action<Task, string> _observe;
    private readonly ProfileService _profiles;
    private readonly Func<bool?> _readOnAcPower;
    private readonly SemaphoreSlim _reconcileGate;
    private readonly Func<string, CancellationToken, bool, Task> _reconcileDesiredValues;
    private readonly Func<HandheldDeviceRuntime?> _runtime;
    private readonly Func<DeviceCycleState> _state;
    private readonly Func<bool> _userCommandsInFlight;

    /// <summary>The runtime the recorded attempts belong to.</summary>
    private HandheldDeviceRuntime? _attemptsRuntime;

    private int _restoreScheduled;

    /// <summary>Composes lighting profiles and lighting restore for one device coordinator.</summary>
    /// <param name="capabilities">The device capability router lighting zones are read from.</param>
    /// <param name="profiles">The profile owner the lighting selection is saved to.</param>
    /// <param name="deviceIdentityKey">Reads the key device values are stored under, or null before identification.</param>
    /// <param name="readOnAcPower">Reads AC/battery status; null means unknown.</param>
    /// <param name="definition">Reads the matched device definition, or null before detection.</param>
    /// <param name="activeProfileScope">Reads the authored profiles of the exact matched model.</param>
    /// <param name="runtime">Reads the active handheld runtime, or null without a cycle.</param>
    /// <param name="integrationEnabled">Whether device integration is switched on.</param>
    /// <param name="state">Reads the device cycle state.</param>
    /// <param name="closed">Whether the coordinator closed admission.</param>
    /// <param name="lifetime">The coordinator's lifetime token, cancelled when admission closes.</param>
    /// <param name="reconcileGate">The desired-state gate shared with every desired-value reconciliation.</param>
    /// <param name="userCommandsInFlight">Whether a user capability command is running.</param>
    /// <param name="execute">Routes a lighting write through the capability commands.</param>
    /// <param name="reconcileDesiredValues">Runs a desired-value pass: reason, cancellation and whether only lighting.</param>
    /// <param name="observe">Tracks fire-and-forget work until coordinator shutdown.</param>
    /// <param name="configurationChanged">Announces a changed lighting profile status to configuration consumers.</param>
    internal DeviceLightingProfiles(
        DeviceCapabilityRouter capabilities,
        ProfileService profiles,
        Func<string?> deviceIdentityKey,
        Func<bool?> readOnAcPower,
        Func<HandheldDefinition?> definition,
        Func<DeviceProfileScope?> activeProfileScope,
        Func<HandheldDeviceRuntime?> runtime,
        Func<bool> integrationEnabled,
        Func<DeviceCycleState> state,
        Func<bool> closed,
        CancellationToken lifetime,
        SemaphoreSlim reconcileGate,
        Func<bool> userCommandsInFlight,
        Func<string, string?, CapabilityValue?, CapabilityCommandOrigin, bool, CancellationToken,
            Task<CapabilityCommandResult>> execute,
        Func<string, CancellationToken, bool, Task> reconcileDesiredValues,
        Action<Task, string> observe,
        Action configurationChanged)
    {
        _capabilities = capabilities;
        _profiles = profiles;
        _deviceIdentityKey = deviceIdentityKey;
        _readOnAcPower = readOnAcPower;
        _definition = definition;
        _activeProfileScope = activeProfileScope;
        _runtime = runtime;
        _integrationEnabled = integrationEnabled;
        _state = state;
        _closed = closed;
        _lifetime = lifetime;
        _reconcileGate = reconcileGate;
        _userCommandsInFlight = userCommandsInFlight;
        _execute = execute;
        _reconcileDesiredValues = reconcileDesiredValues;
        _observe = observe;
        _configurationChanged = configurationChanged;
    }

    /// <summary>The attempt budget of readiness-triggered lighting restores.</summary>
    internal DeviceLightingRestore Restore { get; } = new();

    /// <summary>The lighting profile status text, or null while no profile is selected.</summary>
    internal string? Detail { get; private set; }

    /// <summary>The lighting profile status presentation.</summary>
    internal DescriptorStatus Status { get; private set; }

    private bool Running => !_closed() && _integrationEnabled()
                                       && _state() is DeviceCycleState.Active or DeviceCycleState.Degraded;

    /// <summary>The model's lighting profiles and the effective per-game selection.</summary>
    /// <returns>The profiles and selection, or null before a device definition matched.</returns>
    internal (IReadOnlyList<DeviceAuthoredProfile> Profiles, Resolved<string?> Selected)? Selection()
    {
        return _definition() is null
            ? null
            : (_activeProfileScope()?.Profiles
                    .Where(profile => profile.CapabilityId == CapabilityIds.LightingColor).ToArray() ?? [],
                _profiles.Current.Layers.Reference(values => values.LightingProfileId));
    }

    /// <summary>Saves a lighting profile selection in the layer in force and applies it to every zone.</summary>
    /// <param name="next">The profile to select, or null to inherit.</param>
    /// <param name="cancellationToken">Cancels the save; coordinator shutdown also cancels it.</param>
    /// <returns>Completion once the selection is saved and applied, or a warning status for a missing profile.</returns>
    internal async Task SelectAsync(string? next, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed(), typeof(DeviceCoordinator));
        var selection = Selection();
        if (selection is null ||
            (next is not null && !selection.Value.Profiles.Any(profile => profile.ProfileId == next)))
        {
            SetStatus(
                "The lighting profile no longer exists. Create or select a profile in Settings, Device profiles.",
                DescriptorStatus.Warning);
            return;
        }

        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        await _reconcileGate.WaitAsync(admission.Token).ConfigureAwait(false);
        try
        {
            var snapshot = await _profiles.SetAsync(values => values.LightingProfileId = next,
                $"lighting profile {next ?? "(inherit)"}", cancellationToken: admission.Token).ConfigureAwait(false);
            UpdateDesiredContext(snapshot.Layers);
            await ReconcileProfileAsync(snapshot, admission.Token, true).ConfigureAwait(false);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    /// <summary>A manual zone color replaces a named selection in the layer currently being edited.</summary>
    /// <param name="token">Cancels the save.</param>
    /// <returns>Completion once the selection is cleared, or immediately without one.</returns>
    internal async Task ManualOverrideAsync(CancellationToken token)
    {
        var layers = _profiles.Current.Layers;
        if ((_profiles.Current.EditsGame ? layers.Game?.LightingProfileId : layers.Global.LightingProfileId) is null)
        {
            return;
        }

        await _profiles.SetAsync(values => values.LightingProfileId = null, "manual lighting color",
            cancellationToken: token).ConfigureAwait(false);
        UpdateDesiredContext();
    }

    /// <summary>Publishes the current profile layers, with lighting profiles projected, as the router's desired context.</summary>
    internal void UpdateDesiredContext()
    {
        UpdateDesiredContext(_profiles.Current.Layers);
    }

    /// <summary>Derives zone colors without copying named profile values into saved configuration.</summary>
    /// <param name="layers">The stored profile layers.</param>
    /// <param name="profiles">The model's authored profiles.</param>
    /// <param name="zones">The writable lighting zones.</param>
    /// <param name="identity">The key device values are stored under, or null before identification.</param>
    /// <returns>The layers with each selected profile's color as every zone's desired value.</returns>
    internal static ProfileLayers DesiredLayers(ProfileLayers layers,
        IReadOnlyList<DeviceAuthoredProfile> profiles, IReadOnlyList<CapabilityDescriptor> zones, string? identity)
    {
        return new ProfileLayers(Project(layers.Global), layers.Game is null ? null : Project(layers.Game));

        ProfileValues Project(ProfileValues values)
        {
            var selected = profiles.FirstOrDefault(profile => profile.ProfileId == values.LightingProfileId
                                                              && profile.CapabilityId == CapabilityIds.LightingColor);
            if (selected?.Color is not (>= 0 and <= 0xFFFFFF) || identity is null)
            {
                return values;
            }

            var projected = new ProfileValues
                { Device = [.. values.Device], LightingProfileId = values.LightingProfileId };
            foreach (var zone in zones)
            {
                projected.Device.RemoveAll(entry => entry.DeviceIdentityKey == identity
                                                    && entry.CapabilityId == zone.CapabilityId &&
                                                    entry.InstanceId == zone.InstanceId);
                projected.Device.Add(new ProfileDeviceValue
                {
                    DeviceIdentityKey = identity, CapabilityId = zone.CapabilityId, InstanceId = zone.InstanceId,
                    Value = new CapabilityValue { Kind = CapabilityValueKind.Color, ColorValue = selected.Color }
                });
            }

            return projected;
        }
    }

    /// <summary>Whether a selected lighting profile, not the zone's own desired value, decides this view.</summary>
    /// <param name="view">The capability.</param>
    /// <returns>True for a lighting zone color while a profile is selected.</returns>
    internal bool OwnsProfile(DeviceCapabilityView view)
    {
        return view.Descriptor.Role == CapabilityRole.LightingZoneColor
               && _profiles.Current.Layers.Reference(values => values.LightingProfileId).Value is not null;
    }

    /// <summary>Forgets every recorded profile attempt, so a fresh cycle may apply the profile again.</summary>
    /// <param name="token">Cancels waiting for the desired-state gate.</param>
    /// <returns>Completion once the attempts are cleared.</returns>
    internal async Task RearmAsync(CancellationToken token)
    {
        await _reconcileGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _attempts.Clear();
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    /// <summary>Applies the selected lighting profile's color to every writable zone.</summary>
    /// <param name="snapshot">The profiles the selection resolves from.</param>
    /// <param name="token">Stops before the next zone.</param>
    /// <param name="explicitSelection">Whether the user chose this now, which clears recorded attempts.</param>
    /// <param name="restoreAccepted">Whether a zone already reading the color is written again.</param>
    /// <returns>Completion once every zone was attempted or an uncertain write stopped the pass.</returns>
    /// <remarks>Called under the desired-state gate; no uncertain write is replayed.</remarks>
    internal async Task ReconcileProfileAsync(ProfileSnapshot snapshot, CancellationToken token,
        bool explicitSelection = false, bool restoreAccepted = false)
    {
        if (!explicitSelection && _userCommandsInFlight())
        {
            return;
        }

        var runtime = _runtime();
        if (!ReferenceEquals(_attemptsRuntime, runtime))
        {
            foreach (var key in _attempts.Where(pair => pair.Value.Outcome.IsApplied())
                         .Select(pair => pair.Key).ToArray())
            {
                _attempts.Remove(key);
            }

            _attemptsRuntime = runtime;
        }

        if (explicitSelection)
        {
            _attempts.Clear();
        }

        var selected = snapshot.Layers.Reference(values => values.LightingProfileId);
        if (selected.Value is null)
        {
            SetStatus(null, DescriptorStatus.None);
            return;
        }

        if (!_integrationEnabled() || _runtime() is not { IsActive: true })
        {
            SetStatus("The selected lighting profile will apply when device integration is running.",
                DescriptorStatus.Warning);
            return;
        }

        var profile = _activeProfileScope()?.Profiles.FirstOrDefault(item => item.ProfileId == selected.Value
                                                                            && item.CapabilityId ==
                                                                            CapabilityIds.LightingColor);
        if (profile?.Color is not (>= 0 and <= 0xFFFFFF))
        {
            SetStatus(
                "The selected profile has no valid color. Edit its color in Settings, Device profiles.",
                DescriptorStatus.Warning);
            return;
        }

        UpdateDesiredContext(snapshot.Layers);
        var zones = _capabilities.Snapshot().Where(view => view.Descriptor is
                { Role: CapabilityRole.LightingZoneColor, SupportsWrite: true, ValueKind: CapabilityValueKind.Color })
            .ToArray();
        if (zones.Length == 0)
        {
            SetStatus("This device currently exposes no writable lighting color zones.",
                DescriptorStatus.Warning);
            return;
        }

        var applied = 0;
        string? failure = null;
        var dispatched = false;
        foreach (var zone in zones)
        {
            if (token.IsCancellationRequested)
            {
                failure = "The lighting update was canceled before all zones could be applied.";
                break;
            }

            var key = new DeviceCapabilityKey(zone.Descriptor.CapabilityId, zone.Descriptor.InstanceId);
            var current = _capabilities.TryGetView(key) ?? zone;
            var desired = current.Projection.DesiredValue;
            if (!current.Projection.State.Available || desired?.ColorValue is not { } color)
            {
                failure ??= current.Projection.State.Reason?.Detail ?? "A lighting zone is unavailable.";
                continue;
            }

            var attempted = _attempts.TryGetValue(key, out var attempt);
            if (!explicitSelection && ((attempted && attempt.Color == color && !attempt.Outcome.IsApplied())
                                       || (current.LastResult?.Outcome is CommandOutcome.Indeterminate
                                               or CommandOutcome.TimedOut or CommandOutcome.Rejected
                                               or CommandOutcome.Accepted
                                           && current.LastCommandValue is { } last &&
                                           CapabilityValues.Same(last, desired))))
            {
                failure ??= "A lighting write was refused or uncertain. Select the profile again to retry.";
                if ((attempted && attempt.Outcome is CommandOutcome.Indeterminate or CommandOutcome.TimedOut
                        or CommandOutcome.Accepted)
                    || current.LastResult?.Outcome is CommandOutcome.Indeterminate or CommandOutcome.TimedOut
                        or CommandOutcome.Accepted)
                {
                    break;
                }

                continue;
            }

            if (!explicitSelection && !restoreAccepted
                                   && (current.Projection.State.ObservedValue?.ColorValue == color
                                       || (current.Projection.State.ObservedValue is null
                                           && _attempts.TryGetValue(key, out var accepted)
                                           && accepted.Color == color && accepted.Outcome.IsApplied())))
            {
                applied++;
                continue;
            }

            if (!dispatched)
            {
                dispatched = true;
                SetStatus("Applying the selected color to the current lighting zones.",
                    DescriptorStatus.Progress);
            }

            CapabilityCommandResult result;
            try
            {
                result = await _execute(key.CapabilityId, key.InstanceId, desired,
                    CapabilityCommandOrigin.DesiredStateRestore, false, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                result = new CapabilityCommandResult
                {
                    CommandId = Guid.NewGuid(), Outcome = CommandOutcome.Indeterminate,
                    Reason = new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                        "The lighting write is uncertain: " + exception.Message),
                    CompletedAt = DateTimeOffset.UtcNow
                };
            }

            _attempts[key] = (color, result.Outcome);
            if (result.Outcome.IsApplied())
            {
                applied++;
            }
            else
            {
                failure ??= result.Reason?.Detail ?? "The lighting write was not applied.";
                if (result.Outcome is CommandOutcome.Indeterminate or CommandOutcome.TimedOut
                    or CommandOutcome.Accepted)
                {
                    break;
                }
            }
        }

        SetStatus(failure is null
                ? $"Applied to {applied} lighting zone(s)."
                : $"Applied to {applied} of {zones.Length} lighting zones. {failure}",
            failure is null ? DescriptorStatus.Available : DescriptorStatus.Warning);
    }

    /// <summary>Rewrites every desired lighting value once after a power transition.</summary>
    /// <param name="reason">The transition, for the log.</param>
    /// <remarks>
    ///     A transition can leave lighting at its firmware default, so the replay also writes a value that
    ///     already reads back as desired. It still skips an unavailable or pending zone and never repeats an
    ///     uncertain write.
    /// </remarks>
    internal void ReplayAfterTransition(string reason)
    {
        if (!Running)
        {
            return;
        }

        Restore.Reset();
        _observe(Task.Run(async () =>
        {
            await _reconcileGate.WaitAsync(_lifetime).ConfigureAwait(false);
            try
            {
                await ReconcileProfileAsync(_profiles.Current, _lifetime,
                    restoreAccepted: true).ConfigureAwait(false);
                foreach (var view in _capabilities.Snapshot().Where(view =>
                             DeviceLightingRestore.IsLighting(view.Descriptor.Role)
                             && !OwnsProfile(view)))
                {
                    if (!view.Projection.State.Available || view.Projection.DesiredValue is not { } desired
                                                         || DeviceDesiredWriteAdmission.RepeatsUncertain(view,
                                                             desired)
                                                         || view.Projection.PendingValue is not null)
                    {
                        continue;
                    }

                    await _execute(view.Descriptor.CapabilityId, view.Descriptor.InstanceId, desired,
                        CapabilityCommandOrigin.DesiredStateRestore, false, _lifetime).ConfigureAwait(false);
                }
            }
            finally
            {
                _reconcileGate.Release();
            }
        }), $"lighting restore after {reason}");
    }

    /// <summary>Starts one lighting-only desired-value pass once a lighting zone can be restored.</summary>
    /// <param name="views">The router's newest projection.</param>
    /// <remarks>
    ///     Runs on every state publication, telemetry included, so it avoids LINQ and skips non-lighting
    ///     views before the restore gate is taken.
    /// </remarks>
    internal void OnCapabilitiesChanged(IReadOnlyList<DeviceCapabilityView> views)
    {
        if (!Running
            || _userCommandsInFlight()
            || !HasRestorableLighting(views)
            || Interlocked.CompareExchange(ref _restoreScheduled, 1, 0) != 0)
        {
            return;
        }

        _observe(Task.Run(async () =>
        {
            try
            {
                await _reconcileDesiredValues("lighting ready", _lifetime, true).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _restoreScheduled, 0);
            }
        }), "lighting restore");
    }

    private void UpdateDesiredContext(ProfileLayers layers)
    {
        // Unknown power reads as AC here: only a confirmed battery state selects battery values.
        var zones = _capabilities.Snapshot().Where(view => view.Descriptor is
            { Role: CapabilityRole.LightingZoneColor, SupportsWrite: true }).Select(view => view.Descriptor).ToArray();
        var identity = _deviceIdentityKey();
        _capabilities.UpdateDesiredContext(identity,
            DesiredLayers(layers, _activeProfileScope()?.Profiles ?? [], zones, identity),
            _readOnAcPower() ?? true);
    }

    private bool HasRestorableLighting(IReadOnlyList<DeviceCapabilityView> views)
    {
        for (var index = 0; index < views.Count; index++)
        {
            var view = views[index];
            if (DeviceLightingRestore.IsLighting(view.Descriptor.Role) && Restore.CanApply(view))
            {
                return true;
            }
        }

        return false;
    }

    private void SetStatus(string? detail, DescriptorStatus status)
    {
        if (Detail == detail && Status == status)
        {
            return;
        }

        Detail = detail;
        Status = status;
        if (detail is not null)
        {
            Log.Change("device.lighting-profile", detail,
                status == DescriptorStatus.Warning ? LogLevel.Warn : LogLevel.Info);
        }

        _configurationChanged();
    }
}
