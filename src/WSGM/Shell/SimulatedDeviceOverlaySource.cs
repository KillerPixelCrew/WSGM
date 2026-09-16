using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Settings;
using WSGM.Input;
using WSGM.Overlay;

namespace WSGM.Shell;

/// <summary>In-memory Device surface used only by the explicitly safe overlay-test mode.</summary>
internal sealed class SimulatedDeviceOverlaySource : IDeviceOverlaySource
{
    private int _tdp = 15;
    private bool _lighting = true;
    private int _brightness = 80;
    private int _ringColor = 0xFF9D3D;
    private int _chargeLimit = 80;
    private int _fanMode;
    private int _glyphSelection;
    private bool _autoTdp;
    private ManagedControllerTarget _controllerTarget = ManagedControllerTarget.SteamDeckComposite;
    private string? _hardwareProfile;

    /// <summary>Two named profiles, so the preview shows the cycle rather than a single state.</summary>
    private static readonly string[] PreviewProfiles = ["handheld", "docked"];

    /// <summary>A static six-point monotonic curve, matching the A2VM firmware contract.</summary>
    private static readonly IReadOnlyList<CurvePoint> PreviewCurve =
    [
        new(0, 0),
        new(40, 10),
        new(55, 25),
        new(70, 45),
        new(85, 70),
        new(100, 100)
    ];

    /// <summary>
    /// The layout a real plugin would declare, so --overlay-test exercises sections, categories,
    /// icons, and the mixed case where rows without a placement keep their WSGM fallback home.
    /// </summary>
    private static readonly IReadOnlyList<DeviceOverlayPluginSection> PreviewSections =
    [
        new(
            "power",
            "Power",
            "Performance scenario, power limits, and charging",
            SectionIcon.Power,
            [
                new DeviceOverlayCategory("limits", "Limits"),
                new DeviceOverlayCategory("charging", "Charging")
            ]) { Key = SettingSectionKey.Power },
        new(
            "cooling",
            "Fans",
            "Fan control, curves, and thermal readings",
            SectionIcon.Fan,
            [
                new DeviceOverlayCategory("control", "Control"),
                new DeviceOverlayCategory("readings", "Readings")
            ]),
        new(
            DeviceSections.RgbId,
            "RGB",
            "Ring and button lighting",
            SectionIcon.Lighting,
            [new DeviceOverlayCategory("zones", "Zones")]) { Key = SettingSectionKey.Lighting }
    ];

    public event Action? Changed;

    public DeviceOverlaySnapshot Snapshot()
    {
        string[] fanModes = ["Automatic", "Sport"];
        return new DeviceOverlaySnapshot(
            Visible: true,
            Status: "Simulated handheld",
            Detail: "Preview data only · no plugin activation, hook, or device handle",
            GlyphSelection: new DescriptorRow(
                "device.glyph-selection",
                "Physical glyphs",
                "Preview-only physical presentation selection",
                _glyphSelection switch
                {
                    0 => "AUTO",
                    1 => "STEAM",
                    _ => "REVIEWED"
                },
                CanInvoke: true,
                DescriptorStatus.Available),
            AutoTdp: DeviceOverlayBridge.AutoTdpView(
                _autoTdp,
                _autoTdp
                    ? new AutoTdpStatus(
                        AutoTdpState.Controlling,
                        15,
                        14.2,
                        16.6,
                        "steam:preview",
                        "Preview only; no power write is made.")
                    : null),
            Controller: DeviceOverlayBridge.ControllerView(
                enabled: true,
                new ControllerManagerStatus(
                    ControllerManagementState.Active,
                    _controllerTarget,
                    ControllerTargetSource.GlobalDefault,
                    null,
                    UiInputSource.ManagedCanonical,
                    "Preview only; no virtual controller is created.")),
            // The recovery row is deliberately shown in the preview even though nothing is faulted,
            // because laying it out is exactly what --overlay-test is for. Pressing it does nothing.
            Recovery: DeviceOverlayBridge.RecoveryView(DeviceCycleState.Faulted),
            Profile: DeviceOverlayBridge.ProfileView(PreviewProfiles, _hardwareProfile),
            Capabilities:
            [
                new DeviceOverlayCapability(
                    "preview.power.tdp",
                    null,
                    DeviceOverlaySection.PowerAndThermals,
                    DescriptorStatus.Available,
                    "TDP",
                    "Verified readback · resets on device power loss",
                    $"{_tdp} W",
                    CanInvoke: true,
                    CurrentValue: new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Integer,
                        IntegerValue = _tdp
                    },
                    NextValue: new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Integer,
                        IntegerValue = _tdp >= 30 ? 8 : _tdp + 1
                    })
                {
                    Role = CapabilityRole.PowerSustainedLimit,
                    PluginSectionId = "power",
                    CategoryId = "limits"
                },
                new DeviceOverlayCapability(
                    "preview.battery.charge-limit",
                    null,
                    DeviceOverlaySection.PowerAndThermals,
                    DescriptorStatus.Available,
                    "Charge limit",
                    "Observed · stored on device",
                    $"{_chargeLimit}%",
                    CanInvoke: true,
                    CurrentValue: new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Integer,
                        IntegerValue = _chargeLimit
                    },
                    NextValue: new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Integer,
                        IntegerValue = _chargeLimit >= 100 ? 60 : _chargeLimit + 20
                    })
                {
                    Role = CapabilityRole.ChargeLimit,
                    PluginSectionId = "power",
                    CategoryId = "charging"
                },
                new DeviceOverlayCapability(
                    "preview.fan.mode",
                    null,
                    DeviceOverlaySection.PowerAndThermals,
                    DescriptorStatus.Available,
                    "Fan mode",
                    "Observed · stored on device",
                    fanModes[_fanMode],
                    CanInvoke: true,
                    CurrentValue: new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Choice,
                        ChoiceValue = fanModes[_fanMode]
                    },
                    NextValue: new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Choice,
                        ChoiceValue = fanModes[(_fanMode + 1) % fanModes.Length]
                    })
                {
                    Role = CapabilityRole.FanMode,
                    PluginSectionId = "cooling",
                    CategoryId = "control"
                },
                new DeviceOverlayCapability(
                    "preview.fan.curve",
                    null,
                    DeviceOverlaySection.PowerAndThermals,
                    DescriptorStatus.Available,
                    "Fan curve",
                    "Authored in Settings · both fans follow one curve",
                    $"{PreviewCurve.Count} points",
                    CanInvoke: false,
                    CurrentValue: new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Curve,
                        CurveValue = PreviewCurve
                    },
                    NextValue: null)
                {
                    Role = CapabilityRole.FanCurve,
                    PluginSectionId = "cooling",
                    CategoryId = "control",
                    SortOrder = 1
                },
                new DeviceOverlayCapability(
                    "preview.lighting",
                    null,
                    DeviceOverlaySection.LightingAndFeatures,
                    DescriptorStatus.Available,
                    "Lighting",
                    "Verified readback · stored on device",
                    _lighting ? "ON" : "OFF",
                    CanInvoke: true,
                    CurrentValue: new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Boolean,
                        BooleanValue = _lighting
                    },
                    NextValue: new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Boolean,
                        BooleanValue = !_lighting
                    })
                {
                    Role = CapabilityRole.LightingPower,
                    PluginSectionId = DeviceSections.RgbId
                },
                new DeviceOverlayCapability(
                    "preview.lighting.brightness",
                    null,
                    DeviceOverlaySection.LightingAndFeatures,
                    DescriptorStatus.Available,
                    "Brightness",
                    "Verified readback · stored on device",
                    $"{_brightness}%",
                    CanInvoke: true,
                    CurrentValue: new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Integer,
                        IntegerValue = _brightness
                    },
                    NextValue: new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Integer,
                        IntegerValue = _brightness >= 100 ? 20 : _brightness + 20
                    })
                {
                    Role = CapabilityRole.LightingBrightness,
                    PluginSectionId = DeviceSections.RgbId,
                    SortOrder = 1
                },
                new DeviceOverlayCapability(
                    "preview.lighting.rings",
                    null,
                    DeviceOverlaySection.LightingAndFeatures,
                    DescriptorStatus.Available,
                    "Rings",
                    "Both rings share one color · opens the color editor",
                    $"#{_ringColor:X6}",
                    CanInvoke: true,
                    CurrentValue: new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Color,
                        ColorValue = _ringColor
                    },
                    NextValue: null)
                {
                    Role = CapabilityRole.LightingZoneColor,
                    PluginSectionId = DeviceSections.RgbId,
                    CategoryId = "zones"
                },
                new DeviceOverlayCapability(
                    "preview.temperature.cpu",
                    null,
                    DeviceOverlaySection.PowerAndThermals,
                    DescriptorStatus.Available,
                    "CPU temperature",
                    "Observed · read only",
                    "54 °C",
                    CanInvoke: false,
                    CurrentValue: null,
                    NextValue: null)
                {
                    Role = CapabilityRole.Telemetry,
                    PluginSectionId = "cooling",
                    CategoryId = "readings"
                },
                new DeviceOverlayCapability(
                    "preview.rumble",
                    null,
                    DeviceOverlaySection.ControllerAndMotion,
                    DescriptorStatus.Available,
                    "Rumble",
                    "Short bounded preview action",
                    "RUN",
                    CanInvoke: true,
                    CurrentValue: null,
                    NextValue: null)
                {
                    // Deliberately unplaced: the row proves the WSGM fallback home still renders
                    // beside a declared layout.
                    Role = CapabilityRole.HapticSink
                }
            ])
        {
            GlyphMode = (DeviceGlyphSelection)_glyphSelection,
            PluginSections = PreviewSections
        };
    }

    public Task InvokeAsync(
        DeviceOverlayCapability capability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        cancellationToken.ThrowIfCancellationRequested();
        switch (capability.CapabilityId)
        {
            case "preview.power.tdp":
                _tdp = capability.NextValue?.IntegerValue ?? _tdp;
                break;
            case "preview.fan.mode":
                _fanMode = (_fanMode + 1) % 2;
                break;
            case "preview.lighting":
                _lighting = capability.NextValue?.BooleanValue ?? _lighting;
                break;
            case "preview.battery.charge-limit":
                _chargeLimit = capability.NextValue?.IntegerValue ?? _chargeLimit;
                break;
            case "preview.lighting.brightness":
                _brightness = Math.Clamp(
                    capability.NextValue?.IntegerValue ?? _brightness, 0, 100);
                break;
            case "preview.lighting.rings":
                _ringColor = (capability.NextValue?.ColorValue ?? _ringColor) & 0xFFFFFF;
                break;
        }

        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task ToggleAutoTdpAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _autoTdp = !_autoTdp;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task SetPhysicalGlyphSelectionAsync(DeviceGlyphSelection selection, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(selection)) { throw new ArgumentOutOfRangeException(nameof(selection)); }
        _glyphSelection = (int)selection;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task CycleControllerTargetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _controllerTarget = DeviceOverlayBridge.NextTarget(_controllerTarget);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task CycleHardwareProfileAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _hardwareProfile = DeviceOverlayBridge.NextProfile(PreviewProfiles, _hardwareProfile);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing to cycle: the simulated source has no configuration, so it publishes no authored
    /// profile row and there is no selection for this to advance.
    /// </remarks>
    public Task CycleAuthoredProfileAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>Preview-only: no profile is loaded, so the written letters stand.</summary>
    /// <param name="control">The control the hint names.</param>
    /// <returns>Always null.</returns>
    public PhysicalGlyphRenderPlan? NavigationHint(GlyphControlId control) => null;

    /// <summary>Never raised: the preview reads no device, so there is nothing to observe.</summary>
    public event Action<CanonicalControllerSample>? PhysicalSampleReceived
    {
        add { }
        remove { }
    }

    /// <inheritdoc/>
    public IDisposable ObservePhysicalSamples() => EmptyLease.Instance;

    private sealed class EmptyLease : IDisposable
    {
        internal static readonly EmptyLease Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>Preview-only: there is no device cycle to recover, so this reports and does nothing.</summary>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>A completed task.</returns>
    /// <remarks>
    /// `--overlay-test` exists to lay out the surfaces without starting anything, so the recovery
    /// row is rendered but must stay inert. It is deliberately not an exception: a preview that
    /// threw when a control was pressed would be worse at its one job than one that does nothing.
    /// </remarks>
    public Task RetryDeviceCycleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}
