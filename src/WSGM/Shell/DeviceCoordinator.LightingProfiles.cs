using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Overlay;

namespace WSGM.Shell;

internal sealed partial class DeviceCoordinator
{
    private readonly Dictionary<DeviceCapabilityKey, (int Color, CommandOutcome Outcome)> _lightingProfileAttempts = [];
    private HandheldDeviceRuntime? _lightingProfileRuntime;

    internal string? LightingProfileDetail { get; private set; }

    internal DescriptorStatus LightingProfileStatus { get; private set; }

    internal (IReadOnlyList<DeviceAuthoredProfile> Profiles, Resolved<string?> Selected)? LightingProfileSelection()
    {
        return DeviceDefinition is null
            ? null
            : (ActiveProfileScope()?.Profiles
                    .Where(profile => profile.CapabilityId == CapabilityIds.LightingColor).ToArray() ?? [],
                Profiles.Current.Layers.Reference(values => values.LightingProfileId));
    }

    internal async Task SelectLightingProfileAsync(string? next, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var selection = LightingProfileSelection();
        if (selection is null ||
            (next is not null && !selection.Value.Profiles.Any(profile => profile.ProfileId == next)))
        {
            SetLightingProfileStatus(
                "The lighting profile no longer exists. Create or select a profile in Settings, Device profiles.",
                DescriptorStatus.Warning);
            return;
        }

        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _profileReconcileGate.WaitAsync(admission.Token).ConfigureAwait(false);
        try
        {
            var snapshot = await Profiles.SetAsync(values => values.LightingProfileId = next,
                $"lighting profile {next ?? "(inherit)"}", cancellationToken: admission.Token).ConfigureAwait(false);
            Capabilities.UpdateDesiredContext(DeviceIdentityKey, LightingProfileDesiredLayers(snapshot.Layers),
                _readOnAcPower() ?? true);
            await ReconcileLightingProfileAsync(snapshot, admission.Token, true).ConfigureAwait(false);
        }
        finally
        {
            _profileReconcileGate.Release();
        }
    }

    /// <summary>A manual zone color replaces a named selection in the layer currently being edited.</summary>
    private async Task ManualLightingOverrideAsync(CancellationToken token)
    {
        var layers = Profiles.Current.Layers;
        if ((Profiles.Current.EditsGame ? layers.Game?.LightingProfileId : layers.Global.LightingProfileId) is null)
        {
            return;
        }

        await Profiles.SetAsync(values => values.LightingProfileId = null, "manual lighting color",
            cancellationToken: token).ConfigureAwait(false);
        UpdateCapabilityDesiredContext();
    }

    /// <summary>Derives zone colors without copying named profile values into saved configuration.</summary>
    private ProfileLayers LightingProfileDesiredLayers(ProfileLayers layers)
    {
        var zones = Capabilities.Snapshot().Where(view => view.Descriptor is
            { Role: CapabilityRole.LightingZoneColor, SupportsWrite: true }).Select(view => view.Descriptor).ToArray();
        return LightingProfileDesiredLayers(layers, ActiveProfileScope()?.Profiles ?? [], zones, DeviceIdentityKey);
    }

    internal static ProfileLayers LightingProfileDesiredLayers(ProfileLayers layers,
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

    private bool OwnsLightingProfile(DeviceCapabilityView view)
    {
        return view.Descriptor.Role == CapabilityRole.LightingZoneColor
               && Profiles.Current.Layers.Reference(values => values.LightingProfileId).Value is not null;
    }

    private async Task RearmLightingProfilesAsync(CancellationToken token)
    {
        await _profileReconcileGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _lightingProfileAttempts.Clear();
        }
        finally
        {
            _profileReconcileGate.Release();
        }
    }

    /// <summary>Called under the existing profile reconciliation gate; no uncertain write is replayed.</summary>
    private async Task ReconcileLightingProfileAsync(ProfileSnapshot snapshot, CancellationToken token,
        bool explicitSelection = false, bool restoreAccepted = false)
    {
        if (!explicitSelection && Volatile.Read(ref _userCapabilityCommands) != 0)
        {
            return;
        }

        if (!ReferenceEquals(_lightingProfileRuntime, _client))
        {
            foreach (var key in _lightingProfileAttempts.Where(pair => pair.Value.Outcome.IsApplied())
                         .Select(pair => pair.Key).ToArray())
            {
                _lightingProfileAttempts.Remove(key);
            }

            _lightingProfileRuntime = _client;
        }

        if (explicitSelection)
        {
            _lightingProfileAttempts.Clear();
        }

        var selected = snapshot.Layers.Reference(values => values.LightingProfileId);
        if (selected.Value is null)
        {
            SetLightingProfileStatus(null, DescriptorStatus.None);
            return;
        }

        if (!IntegrationEnabled || _client is not { IsActive: true })
        {
            SetLightingProfileStatus("The selected lighting profile will apply when device integration is running.",
                DescriptorStatus.Warning);
            return;
        }

        var profile = ActiveProfileScope()?.Profiles.FirstOrDefault(item => item.ProfileId == selected.Value
                                                                            && item.CapabilityId ==
                                                                            CapabilityIds.LightingColor);
        if (profile?.Color is not (>= 0 and <= 0xFFFFFF))
        {
            SetLightingProfileStatus(
                "The selected profile has no valid color. Edit its color in Settings, Device profiles.",
                DescriptorStatus.Warning);
            return;
        }

        Capabilities.UpdateDesiredContext(DeviceIdentityKey, LightingProfileDesiredLayers(snapshot.Layers),
            _readOnAcPower() ?? true);
        var zones = Capabilities.Snapshot().Where(view => view.Descriptor is
                { Role: CapabilityRole.LightingZoneColor, SupportsWrite: true, ValueKind: CapabilityValueKind.Color })
            .ToArray();
        if (zones.Length == 0)
        {
            SetLightingProfileStatus("This device currently exposes no writable lighting color zones.",
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
            var current = Capabilities.TryGetView(key) ?? zone;
            var desired = current.Projection.DesiredValue;
            if (!current.Projection.State.Available || desired?.ColorValue is not { } color)
            {
                failure ??= current.Projection.State.Reason?.Detail ?? "A lighting zone is unavailable.";
                continue;
            }

            var attempted = _lightingProfileAttempts.TryGetValue(key, out var attempt);
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
                                           && _lightingProfileAttempts.TryGetValue(key, out var accepted)
                                           && accepted.Color == color && accepted.Outcome.IsApplied())))
            {
                applied++;
                continue;
            }

            if (!dispatched)
            {
                dispatched = true;
                SetLightingProfileStatus("Applying the selected color to the current lighting zones.",
                    DescriptorStatus.Progress);
            }

            CapabilityCommandResult result;
            try
            {
                result = await ExecuteCapabilityAsync(key.CapabilityId, key.InstanceId, desired,
                    TimeSpan.FromSeconds(5), CapabilityCommandOrigin.DesiredStateRestore,
                    cancellationToken: token).ConfigureAwait(false);
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

            _lightingProfileAttempts[key] = (color, result.Outcome);
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

        SetLightingProfileStatus(failure is null
                ? $"Applied to {applied} lighting zone(s)."
                : $"Applied to {applied} of {zones.Length} lighting zones. {failure}",
            failure is null ? DescriptorStatus.Available : DescriptorStatus.Warning);
    }

    private void SetLightingProfileStatus(string? detail, DescriptorStatus status)
    {
        if (LightingProfileDetail == detail && LightingProfileStatus == status)
        {
            return;
        }

        LightingProfileDetail = detail;
        LightingProfileStatus = status;
        if (detail is not null)
        {
            Log.Change("device.lighting-profile", detail,
                status == DescriptorStatus.Warning ? LogLevel.Warn : LogLevel.Info);
        }

        ConfigurationChanged?.Invoke();
    }
}
