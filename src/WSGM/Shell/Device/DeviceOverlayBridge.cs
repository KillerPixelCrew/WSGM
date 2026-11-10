using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibHandheld.Contracts;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Lifecycle;
using CanonicalControllerSample = LibHandheld.Contracts.CanonicalControllerSample;
using CapabilityRole = WSGM.Device.Sdk.Capabilities.CapabilityRole;

namespace WSGM.Shell;

/// <summary>
///     Adapts the authoritative coordinator to the overlay without exposing transport or plugin data.
/// </summary>
internal sealed class DeviceOverlayBridge : IDeviceOverlaySource
{
    private readonly DeviceCoordinator _coordinator;
    private readonly PhysicalGlyphPlans _glyphs;
    private readonly GpuCoordinator? _gpu;
    private ExtendedVrrService? _extendedVrr;
    private readonly Lock _sampleGate = new();
    private bool _disposed;
    private int _sampleObservers;

    /// <summary>Subscribes a presentation adapter to existing device, glyph, power, and optional graphics owners.</summary>
    /// <param name="coordinator">The borrowed device coordinator; disposal removes subscriptions without stopping it.</param>
    /// <param name="gpu">
    ///     The graphics coordinator, whose variable-refresh capability the Power page shows
    ///     when the device publishes none; null without one.
    /// </param>
    internal DeviceOverlayBridge(DeviceCoordinator coordinator, GpuCoordinator? gpu = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
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
        // The AutoTDP row renders live state, not the stored setting, and that state changes
        // with no capability view and no configuration change behind it.
        _coordinator.Power.AutoTdp.StatusChanged += OnAutoTdpStatusChanged;

        // The AutoTDP target is a profile value: a game switch or Use global moves it and its marker
        // without touching the device.
        _coordinator.Profiles.Changed += OnProfilesChanged;
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>Adds the extended VRR range, created after this bridge with the display pairing.</summary>
    /// <param name="extendedVrr">The owner Steam's Display page uses too.</param>
    internal void AttachExtendedVrr(ExtendedVrrService extendedVrr)
    {
        ArgumentNullException.ThrowIfNull(extendedVrr);
        _extendedVrr = extendedVrr;
        extendedVrr.Changed += OnExtendedVrrChanged;
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public DeviceOverlaySnapshot Snapshot()
    {
        var state = _coordinator.State;
        var device = _coordinator.DeviceDefinition;
        var controllerStatus = _coordinator.Controllers.Snapshot();
        // The shared pages are always among them, so every row below lands on a page.
        var declaredSections = _coordinator.Capabilities.Sections;
        HashSet<string> declaredSectionIds = new(declaredSections.Select(section => section.SectionId),
            StringComparer.Ordinal);
        var deviceViews = _coordinator.PresentedCapabilities();
        var presets = _coordinator.FanPresets;
        var capabilities = deviceViews
            .Select(view => CapabilityRowProjection.ToDeviceRow(view, declaredSectionIds, presets,
                _coordinator.Profiles.Current.Layers))
            .ToList();
        // Variable refresh moved to the graphics packages; the Power row stays where users look for it,
        // backed by whichever package publishes the capability.
        if (deviceViews.All(view => view.Descriptor.Role is not CapabilityRole.VariableRefreshRate)
            && VariableRefreshCapabilities.Find(null, _gpu, false) is { GpuPluginId: { } vrrPlugin } vrr)
        {
            capabilities.Add(VariableRefreshRow(vrr.View, vrrPlugin, declaredSectionIds, presets,
                _coordinator.Profiles.Current.Layers));
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
        var autoTdpAvailability = _coordinator.Power.AutoTdp.Availability;
        var autoTdp = DeviceHostRows.AutoTdpView(
            _coordinator.Power.AutoTdpEnabled,
            _coordinator.Power.AutoTdp.Status,
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

        var detail = device is null
            ? state is DeviceCycleState.Detected or DeviceCycleState.Passive
                ? "No compatible handheld backend is active."
                : "Device integration is waiting for a compatible handheld."
            : device.Name;
        var snapshot = new DeviceOverlaySnapshot(
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
                    DeviceHostRows.TargetChoices(_coordinator.Controllers.SupportedTargets)
                        .Select(option => DeviceHostRows.HostChoice(option.Target.ToString(), option.Label))
                        .ToArray()),
                [DeviceHostRowIds.AuthoredProfile] = new(authored?.Selected.Value ?? "",
                    DeviceHostRows.ProfileChoices(authored?.Profiles, "None")),
                [DeviceHostRowIds.LightingProfile] = new(lighting?.Selected.Value ?? "",
                    DeviceHostRows.ProfileChoices(lighting?.Profiles,
                        DeviceHostRows.LightingProfileEmptyLabel(_coordinator.Profiles.Current.EditsGame))),
                [DeviceHostRowIds.GlyphSelection] = new(_coordinator.PhysicalGlyphSelection.ToString(),
                    DeviceHostRows.GlyphChoices()),
                [DeviceHostRowIds.ExtendedVrr] = new(_extendedVrr?.Status.Enabled == true ? "on" : "off",
                    [DeviceHostRows.HostChoice("off", "Off"), DeviceHostRows.HostChoice("on", "On")]),
                [DeviceHostRowIds.ExtendedVrrLowFrameRate] = new(
                    _extendedVrr?.Status.LowFrameRateCompensation == true ? "on" : "off",
                    [DeviceHostRows.HostChoice("off", "Off"), DeviceHostRows.HostChoice("on", "On")])
            },
            LightingProfile = lighting is { } colors
                ? DeviceHostRows.LightingProfileView(colors.Profiles, colors.Selected,
                    _coordinator.Lighting.Detail, _coordinator.Lighting.Status)
                : null,
            FanTemperature = _coordinator.FanCurves.Temperature().Celsius is { } celsius
                ? (int)Math.Round(celsius)
                : null,
            PluginSections = CapabilityRowProjection.ProjectSections(declaredSections),
            ExtendedVrr = _extendedVrr is { } extendedVrr ? DeviceHostRows.ExtendedVrrView(extendedVrr.Status) : null,
            ExtendedVrrLowFrameRate = _extendedVrr is { } range
                ? DeviceHostRows.ExtendedVrrLowFrameRateView(range.Status)
                : null,
            AutoTdpTarget = DeviceHostRows.AutoTdpTargetRow(
                _coordinator.Power.AutoTdpEnabled,
                AutoTdpTargetView.Resolve(_coordinator.Profiles.Current.Layers, autoTdpAvailability))
        };

        // With integration off there is no AutoTDP, device profile or recovery to offer. The controller
        // page keeps its glyph and controller rows, which do not need a running device.
        return snapshot.Visible
            ? snapshot
            : snapshot with
            {
                AutoTdp = null, AutoTdpTarget = null, AuthoredProfile = null, LightingProfile = null, Recovery = null,
                ExtendedVrr = null, ExtendedVrrLowFrameRate = null
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

        var current = _coordinator.Capabilities.TryGetView(
            new DeviceCapabilityKey(capability.CapabilityId, capability.InstanceId));
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
            DeviceHostRowIds.ExtendedVrr when value is "on" or "off" && _extendedVrr is { } extendedVrr =>
                extendedVrr.SetEnabledAsync(value == "on", cancellationToken),
            DeviceHostRowIds.ExtendedVrrLowFrameRate when value is "on" or "off" && _extendedVrr is { } range =>
                range.SetLowFrameRateCompensationAsync(value == "on", cancellationToken),
            DeviceHostRowIds.GlyphSelection when Enum.TryParse<DeviceGlyphSelection>(value, out var glyphs)
                                                 && Enum.IsDefined(glyphs) =>
                _coordinator.SetPhysicalGlyphSelectionAsync(glyphs, cancellationToken),
            _ => Task.CompletedTask
        };
    }

    /// <inheritdoc />
    public Task SetAutoTdpTargetAsync(int fps, CancellationToken cancellationToken = default)
    {
        return _coordinator.Power.SetAutoTdpTargetAsync(fps, cancellationToken);
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
                _coordinator.Controllers.Forwarding.PhysicalSampleObserved += OnPhysicalSample;
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

        _extendedVrr?.Changed -= OnExtendedVrrChanged;
        _coordinator.StateChanged -= OnStateChanged;
        _coordinator.Capabilities.Changed -= OnCapabilityViewsChanged;
        _coordinator.ConfigurationChanged -= OnConfigurationChanged;
        _coordinator.Profiles.Changed -= OnProfilesChanged;
        _coordinator.Power.AutoTdp.StatusChanged -= OnAutoTdpStatusChanged;

        lock (_sampleGate)
        {
            if (_sampleObservers > 0)
            {
                _coordinator.Controllers.Forwarding.PhysicalSampleObserved -= OnPhysicalSample;
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

    /// <summary>A graphics package's variable refresh capability as the Device page shows it.</summary>
    /// <param name="view">The graphics capability.</param>
    /// <param name="gpuPluginId">The graphics plugin that publishes it, which commands route to.</param>
    /// <param name="declaredSections">The device publisher's sections.</param>
    /// <param name="presets">The matched model's fan curve presets.</param>
    /// <param name="layers">Current profile layers for the game-override marker, or null when unavailable.</param>
    /// <returns>
    ///     The row on the shared page of its role: the graphics package's own sections mean nothing on the
    ///     Device page.
    /// </returns>
    internal static DeviceOverlayCapability VariableRefreshRow(DeviceCapabilityView view, string gpuPluginId,
        IReadOnlySet<string> declaredSections, FanCurvePresetTable presets, ProfileLayers? layers = null)
    {
        return CapabilityRowProjection.OnDevicePage(
            CapabilityRowProjection.ToOverlayCapability(view, declaredSections, presets, layers) with
            {
                GpuPluginId = gpuPluginId, PluginSectionId = null, CategoryId = null
            });
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

            _coordinator.Controllers.Forwarding.PhysicalSampleObserved -= OnPhysicalSample;
            _coordinator.PhysicalOemEventReceived -= OnPhysicalOemEvent;
            _coordinator.Controllers.StatusChanged -= OnPhysicalControllerStatus;
        }

        PhysicalInputReset?.Invoke();
    }

    private void OnPhysicalSample(CanonicalControllerSample sample)
    {
        PhysicalSampleReceived?.Invoke(sample);
    }

    private void OnPhysicalOemEvent(OemControlEvent input)
    {
        if (PhysicalGlyphPlans.TryGetOemControl(_coordinator.PhysicalGlyphSelectionSnapshot(), input.ControlId,
                out var control))
        {
            PhysicalOemControlReceived?.Invoke(control, input.Edge is OemControlEdge.Pressed);
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

    private void OnExtendedVrrChanged()
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
