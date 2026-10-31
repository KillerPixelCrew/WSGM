using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Lifecycle;
using CanonicalControllerSample = LibHandheld.Contracts.CanonicalControllerSample;

namespace WSGM.Shell;

/// <summary>
///     Adapts the authoritative coordinator to the overlay without exposing transport or plugin data.
/// </summary>
internal sealed class DeviceOverlayBridge : IDeviceOverlaySource
{
    private readonly AutoTdpService? _autoTdp;
    private readonly DeviceCoordinator _coordinator;
    private readonly PhysicalGlyphPlans _glyphs;
    private readonly GpuCoordinator? _gpu;
    private readonly Lock _sampleGate = new();
    private bool _disposed;
    private int _sampleObservers;

    /// <summary>Subscribes a presentation adapter to existing device, glyph, power, and optional graphics owners.</summary>
    /// <param name="coordinator">The borrowed device coordinator; disposal removes subscriptions without stopping it.</param>
    /// <param name="autoTdp">AutoTDP, or null when it is not running.</param>
    /// <param name="gpu">
    ///     The graphics coordinator, whose variable-refresh capability the Power and thermals section shows
    ///     when the device publishes none; null without one.
    /// </param>
    internal DeviceOverlayBridge(DeviceCoordinator coordinator, AutoTdpService? autoTdp, GpuCoordinator? gpu = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
        _autoTdp = autoTdp;
        _gpu = gpu;
        if (_gpu is not null)
        {
            _gpu.Changed += OnGpuChanged;
        }

        // One service over the coordinator's catalog, so its bounded geometry cache is shared by
        // every preview and is invalidated by the same catalog change that replaces the profiles.
        _glyphs = new PhysicalGlyphPlans(coordinator.PhysicalGlyphCatalog);
        _coordinator.StateChanged += OnStateChanged;
        _coordinator.Capabilities.Changed += OnCapabilityViewsChanged;
        _coordinator.ConfigurationChanged += OnConfigurationChanged;
        if (_autoTdp is not null)
        {
            // The AutoTDP row renders live state, not the stored setting, and that state changes
            // with no capability view and no configuration change behind it.
            _autoTdp.StatusChanged += OnAutoTdpStatusChanged;
        }

        // The AutoTDP target is a profile value: a game switch or Use global moves it and its marker
        // without touching the device.
        _coordinator.Profiles.Changed += OnProfilesChanged;
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public Task CycleAuthoredProfileAsync(CancellationToken cancellationToken = default)
    {
        return _coordinator.FanCurves.CycleAuthoredProfileAsync(cancellationToken);
    }

    /// <inheritdoc />
    public DeviceOverlaySnapshot Snapshot()
    {
        var state = _coordinator.State;
        var device = _coordinator.DeviceDefinition;
        var controllerStatus = _coordinator.Controllers.Snapshot();
        var declaredSections = _coordinator.Capabilities.Sections;
        HashSet<string> declaredSectionIds = new(
            DeviceSectionLayout.IncludePredefined(declaredSections).Select(section => section.SectionId),
            StringComparer.Ordinal);
        var deviceViews = _coordinator.PresentedCapabilities();
        var presets = _coordinator.FanPresets;
        var capabilities = deviceViews
            .Select(view => CapabilityRowProjection.ToOverlayCapability(view, declaredSectionIds, presets,
                _coordinator.Profiles.Current.Layers))
            .ToList();
        // Variable refresh moved to the graphics packages; the Power and thermals row stays where users
        // look for it, backed by whichever package publishes the capability.
        if (deviceViews.All(view => view.Descriptor.Role is not CapabilityRole.VariableRefreshRate)
            && VariableRefreshCapabilities.Find(null, _gpu, false) is { GpuPluginId: { } vrrPlugin } vrr)
        {
            capabilities.Add(CapabilityRowProjection.ToOverlayCapability(vrr.View, declaredSectionIds, presets,
                    _coordinator.Profiles.Current.Layers)
                with
                {
                    GpuPluginId = vrrPlugin,
                    PluginSectionId = null,
                    CategoryId = null
                });
        }

        if (_coordinator.Power.ManualTdpUnified)
        {
            for (var index = 0; index < capabilities.Count; index++)
            {
                capabilities[index] = CapabilityRowProjection.ProjectManualTdp(capabilities[index], true);
            }
        }

        var glyphSelectionState = _coordinator.PhysicalGlyphSelectionSnapshot();
        var glyphSelection = DeviceHostRows.PhysicalGlyphSelectionView(
            _coordinator.PhysicalGlyphSelection,
            glyphSelectionState);
        var autoTdpAvailability = _autoTdp?.Availability;
        var autoTdp = DeviceHostRows.AutoTdpView(
            _coordinator.Power.AutoTdpEnabled,
            _autoTdp?.Status,
            autoTdpAvailability);
        var recovery = DeviceHostRows.RecoveryView(state);
        var controller = DeviceHostRows.ControllerView(
            _coordinator.ControllerManagementEnabled,
            controllerStatus) is { } controllerRow
            ? controllerRow with
            {
                OverrideId = controllerStatus.TargetSource is ProfileSource.Game
                    ? nameof(ProfileField.ControllerTarget)
                    : null
            }
            : null;
        var authored = _coordinator.FanCurves.AuthoredProfileSelection();
        var lighting = _coordinator.Lighting.Selection();
        var glyphPreview = DeviceHostRows.GlyphPreview(
            glyphSelectionState,
            _glyphs,
            // The input test is live only while the plugin's canonical samples are actually
            // reaching WSGM. Offering it otherwise would show a map that can never light up.
            controllerStatus.State is ControllerManagementState.Active);

        // OrderBy is stable, so rows keep their order within a section.
        capabilities = [.. capabilities.OrderBy(capability => capability.Section)];
        var detail = device is null
            ? state is DeviceCycleState.Detected or DeviceCycleState.Passive
                ? "No compatible handheld backend is active."
                : "Device integration is waiting for a compatible handheld."
            : device.Name;
        return new DeviceOverlaySnapshot(
            _coordinator.IntegrationEnabled,
            DeviceHostRows.LifecycleLabel(state),
            detail,
            glyphSelection,
            capabilities,
            autoTdp,
            controller,
            recovery,
            glyphPreview,
            authored is { } selection
                ? DeviceHostRows.AuthoredProfileView(
                    selection.Profiles,
                    selection.Selected.Value,
                    selection.Selected.Source) is { } authoredRow
                    ? authoredRow with
                    {
                        OverrideId = selection.Selected.IsGameOverride ? nameof(ProfileField.FanCurveProfile) : null
                    }
                    : null
                : null)
        {
            HostSelections = new Dictionary<string, DeviceHostSelection>
            {
                [DeviceHostRowIds.AutoTdp] = new(_coordinator.Power.AutoTdpEnabled ? "on" : "off",
                    [DeviceHostRows.HostChoice("off", "Off"), DeviceHostRows.HostChoice("on", "On")]),
                [DeviceHostRowIds.ControllerTarget] = new(
                    (controllerStatus.Target ?? _coordinator.ChosenControllerTarget()).ToString(),
                    _coordinator.Controllers.SupportedTargets
                        .Select(target => DeviceHostRows.HostChoice(target.ToString(),
                            DeviceHostRows.TargetLabel(target)))
                        .ToArray()),
                [DeviceHostRowIds.AuthoredProfile] = new(authored?.Selected.Value ?? "",
                    DeviceHostRows.ProfileChoices(authored?.Profiles, "None")),
                [DeviceHostRowIds.LightingProfile] = new(lighting?.Selected.Value ?? "",
                    DeviceHostRows.ProfileChoices(lighting?.Profiles,
                        DeviceHostRows.LightingProfileEmptyLabel(_coordinator.Profiles.Current.EditsGame)))
            },
            LightingProfile = lighting is { } colors
                ? DeviceHostRows.LightingProfileView(colors.Profiles, colors.Selected,
                    _coordinator.Lighting.Detail, _coordinator.Lighting.Status)
                : null,
            GlyphMode = _coordinator.PhysicalGlyphSelection,
            PluginSections = CapabilityRowProjection.ProjectSections(declaredSections),
            AutoTdpTarget = DeviceHostRows.AutoTdpTargetRow(
                _coordinator.Power.AutoTdpEnabled,
                AutoTdpTargetView.Resolve(_coordinator.Profiles.Current.Layers, autoTdpAvailability))
        };
    }

    /// <inheritdoc />
    public async Task InvokeAsync(
        DeviceOverlayCapability capability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        if (!capability.CanInvoke)
        {
            return;
        }

        if (capability.GpuPluginId is { } pluginId)
        {
            await InvokeGpuAsync(pluginId, capability, cancellationToken).ConfigureAwait(false);
            return;
        }

        var current = _coordinator.Capabilities.Snapshot().FirstOrDefault(view =>
            view.Descriptor.CapabilityId == capability.CapabilityId &&
            view.Descriptor.InstanceId == capability.InstanceId);
        if (current is null)
        {
            // A deferred editor callback belongs to the descriptor the user actually saw.
            Changed?.Invoke();
            return;
        }

        await _coordinator.Commands.ExecuteAsync(
            capability.CapabilityId,
            capability.InstanceId,
            capability.NextValue,
            TimeSpan.FromSeconds(5),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SetHostSelectionAsync(string rowId, string? value, CancellationToken cancellationToken = default)
    {
        return rowId switch
        {
            DeviceHostRowIds.AutoTdp when value is "on" or "off" =>
                _coordinator.Power.SetAutoTdpEnabledAsync(value == "on", cancellationToken),
            DeviceHostRowIds.ControllerTarget when Enum.TryParse<ManagedControllerTarget>(value, out var target)
                                                   && _coordinator.Controllers.SupportedTargets.Contains(target) =>
                _coordinator.SetControllerTargetAsync(target, cancellationToken),
            DeviceHostRowIds.AuthoredProfile => _coordinator.FanCurves.SelectAuthoredProfileAsync(
                string.IsNullOrEmpty(value) ? null : value, cancellationToken),
            DeviceHostRowIds.LightingProfile => _coordinator.Lighting.SelectAsync(
                string.IsNullOrEmpty(value) ? null : value, cancellationToken),
            _ => Task.CompletedTask
        };
    }

    /// <inheritdoc />
    public Task SetPhysicalGlyphSelectionAsync(DeviceGlyphSelection selection,
        CancellationToken cancellationToken = default)
    {
        return _coordinator.SetPhysicalGlyphSelectionAsync(selection, cancellationToken);
    }

    /// <inheritdoc />
    public Task ToggleAutoTdpAsync(CancellationToken cancellationToken = default)
    {
        return _coordinator.Power.ToggleAutoTdpAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task SetAutoTdpTargetAsync(int fps, CancellationToken cancellationToken = default)
    {
        return _coordinator.Power.SetAutoTdpTargetAsync(fps, cancellationToken);
    }

    /// <inheritdoc />
    public Task CycleControllerTargetAsync(CancellationToken cancellationToken = default)
    {
        return _coordinator.SetControllerTargetAsync(
            DeviceHostRows.NextTarget(
                _coordinator.Controllers.Snapshot().Target,
                _coordinator.Controllers.SupportedTargets),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task RetryDeviceCycleAsync(CancellationToken cancellationToken = default)
    {
        return _coordinator.RetryAfterFaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task UseGlobalAsync(string overrideId, CancellationToken cancellationToken = default)
    {
        return ProfileSettingKey.TryParse(overrideId, out var key)
            ? _coordinator.Profiles.ClearGameOverrideAsync(
                key,
                key.Field is ProfileField.Device ? _coordinator.DeviceIdentityKey : null,
                cancellationToken)
            : Task.CompletedTask;
    }

    /// <inheritdoc />
    public PhysicalGlyphRenderPlan? NavigationHint(GlyphControlId control)
    {
        if (_disposed)
        {
            return null;
        }

        // The NavigationHint surface carries its own authorization: the service refuses it unless
        // the active input source is the managed handheld, which is exactly the condition under
        // which replacing a written letter with a device glyph is correct.
        var plan = _glyphs.Resolve(
            _coordinator.PhysicalGlyphSelectionSnapshot(),
            control,
            PhysicalGlyphSurface.NavigationHint,
            _coordinator.Controllers.Snapshot().State is ControllerManagementState.Active);
        return plan.UsesDeviceArtwork ? plan : null;
    }

    /// <inheritdoc />
    public event Action<CanonicalControllerSample>? PhysicalSampleReceived;

    /// <inheritdoc />
    public event Action<GlyphControlId, bool>? PhysicalOemControlReceived;

    /// <inheritdoc />
    public event Action? PhysicalInputReset;

    /// <inheritdoc />
    public IDisposable ObservePhysicalSamples()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sampleGate)
        {
            if (_sampleObservers++ == 0)
            {
                _coordinator.Controllers.PhysicalSampleObserved += OnPhysicalSample;
                _coordinator.PhysicalOemEventReceived += OnPhysicalOemEvent;
                _coordinator.Controllers.StatusChanged += OnPhysicalControllerStatus;
            }
        }

        return new SampleLease(this);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_gpu is not null)
        {
            _gpu.Changed -= OnGpuChanged;
        }

        _coordinator.StateChanged -= OnStateChanged;
        _coordinator.Capabilities.Changed -= OnCapabilityViewsChanged;
        _coordinator.ConfigurationChanged -= OnConfigurationChanged;
        _coordinator.Profiles.Changed -= OnProfilesChanged;
        if (_autoTdp is not null)
        {
            _autoTdp.StatusChanged -= OnAutoTdpStatusChanged;
        }

        lock (_sampleGate)
        {
            if (_sampleObservers > 0)
            {
                _coordinator.Controllers.PhysicalSampleObserved -= OnPhysicalSample;
                _coordinator.PhysicalOemEventReceived -= OnPhysicalOemEvent;
                _coordinator.Controllers.StatusChanged -= OnPhysicalControllerStatus;
                _sampleObservers = 0;
            }
        }

        // The plans subscribed to the catalog's change event, so it has to be released here or it
        // keeps this bridge's geometry cache alive for the rest of the session.
        _glyphs.Dispose();
        PhysicalInputReset?.Invoke();
    }

    private async Task InvokeGpuAsync(string pluginId, DeviceOverlayCapability capability,
        CancellationToken cancellationToken)
    {
        var current = _gpu?.Snapshot(pluginId)?.Capabilities.FirstOrDefault(candidate =>
            candidate.View.Descriptor.CapabilityId == capability.CapabilityId
            && candidate.View.Descriptor.InstanceId == capability.InstanceId)?.View;
        if (_gpu is null || current is null)
        {
            Changed?.Invoke();
            return;
        }

        await _gpu.ExecuteAsync(pluginId, capability.CapabilityId, capability.InstanceId, capability.NextValue,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private void ReleasePhysicalSamples()
    {
        lock (_sampleGate)
        {
            if (_sampleObservers == 0 || --_sampleObservers > 0)
            {
                return;
            }

            _coordinator.Controllers.PhysicalSampleObserved -= OnPhysicalSample;
            _coordinator.PhysicalOemEventReceived -= OnPhysicalOemEvent;
            _coordinator.Controllers.StatusChanged -= OnPhysicalControllerStatus;
        }

        PhysicalInputReset?.Invoke();
    }

    private void OnPhysicalSample(CanonicalControllerSample sample)
    {
        PhysicalSampleReceived?.Invoke(sample);
    }

    private void OnPhysicalOemEvent(LibHandheld.Contracts.OemControlEvent input)
    {
        if (PhysicalGlyphPlans.TryGetOemControl(_coordinator.PhysicalGlyphSelectionSnapshot(), input.ControlId,
                out var control))
        {
            PhysicalOemControlReceived?.Invoke(control, input.Edge is LibHandheld.Contracts.OemControlEdge.Pressed);
        }
    }

    private void OnPhysicalControllerStatus(ControllerManagerStatus status)
    {
        if (status.State is not ControllerManagementState.Active)
        {
            PhysicalInputReset?.Invoke();
        }
    }

    private void OnStateChanged(DeviceCycleState _)
    {
        PhysicalInputReset?.Invoke();
        Changed?.Invoke();
    }

    private void OnCapabilityViewsChanged(IReadOnlyList<DeviceCapabilityView> _)
    {
        Changed?.Invoke();
    }

    private void OnConfigurationChanged()
    {
        PhysicalInputReset?.Invoke();
        Changed?.Invoke();
    }

    // Raised on the UI dispatcher by the graphics coordinator.
    private void OnGpuChanged()
    {
        Changed?.Invoke();
    }

    // Raised from AutoTDP's own tick loop. The overlay, the one subscriber, marshals its own refresh.
    private void OnAutoTdpStatusChanged(AutoTdpStatus _)
    {
        Changed?.Invoke();
    }

    private void OnProfilesChanged(ProfileSnapshot snapshot, ProfileChangeKind kind)
    {
        Changed?.Invoke();
    }

    /// <summary>One observer's claim on the physical sample stream.</summary>
    /// <remarks>
    ///     Idempotent, because a surface torn down twice — closed and then disposed — must not push the
    ///     count below zero and detach a subscription another surface still holds.
    /// </remarks>
    private sealed class SampleLease(DeviceOverlayBridge owner) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            owner.ReleasePhysicalSamples();
        }
    }
}
