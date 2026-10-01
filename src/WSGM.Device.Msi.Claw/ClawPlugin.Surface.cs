using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Services;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Device.Msi.Claw;

// The capability surface: descriptors, published states and OEM controls.
public sealed partial class ClawPlugin
{
    /// <summary>The Claw's declared Device overlay layout.</summary>
    /// <remarks>
    ///     Titles and icons are WSGM-owned vocabulary; only the grouping is this plugin's. A section a
    ///     firmware variant leaves empty (Display without an ARC Sync panel) is dropped by WSGM rather
    ///     than declared conditionally, so the layout stays one static fact.
    /// </remarks>
    private static readonly IReadOnlyList<CapabilitySection> OverlaySections =
    [
        DeviceSections.Power with
        {
            Categories =
            [
                new CapabilityCategory
                {
                    CategoryId = CategoryIds.Limits,
                    Key = SettingSectionKey.Custom,
                    CustomTitle = "Limits",
                    SortOrder = 0
                },
                new CapabilityCategory
                {
                    CategoryId = CategoryIds.Charging,
                    Key = SettingSectionKey.Custom,
                    CustomTitle = "Charging",
                    SortOrder = 1
                },
                // Fans and thermals were their own Cooling section; folded in here so Power is one
                // page instead of two that both read as "power" to the user (maintainer-directed).
                // The readings that used to follow them moved to Info for the same reason: this is
                // the page for changing how the device behaves, not for watching it.
                new CapabilityCategory
                {
                    CategoryId = CategoryIds.Control,
                    Key = SettingSectionKey.Custom,
                    CustomTitle = "Fans",
                    SortOrder = 2
                }
            ]
        },
        DeviceSections.Rgb with
        {
            Categories =
            [
                new CapabilityCategory
                {
                    CategoryId = CategoryIds.Zones,
                    Key = SettingSectionKey.Custom,
                    CustomTitle = "Zones",
                    SortOrder = 0
                }
            ]
        },
        DeviceSections.Info with
        {
            Categories =
            [
                new CapabilityCategory
                {
                    CategoryId = CategoryIds.Ownership,
                    Key = SettingSectionKey.Custom,
                    CustomTitle = "Plugin ownership",
                    SortOrder = 0
                },
                new CapabilityCategory
                {
                    CategoryId = CategoryIds.Readings,
                    Key = SettingSectionKey.Custom,
                    CustomTitle = "Readings",
                    SortOrder = 1
                }
            ]
        }
    ];

    /// <summary>Intel's gaming-flip flags, in the order a user would read them.</summary>
    /// <remarks>
    ///     Keyed by the flag bit, so the stable choice value never depends on Intel's ordering. Only the
    ///     bits this driver reports supported are offered, measured as <c>0x2d</c> on the reference
    ///     unit, which is application default, VSync on, Smooth Sync and capped FPS. Notably absent is
    ///     "VSync off": leaving it off is what the application default already means, so the driver
    ///     offers forcing it on rather than forcing it off.
    /// </remarks>
    private static readonly (uint Bit, string Value)[] FlipModes =
    [
        (1u << 0, "application-default"),
        (1u << 2, "vsync-on"),
        (1u << 3, "smooth-sync"),
        (1u << 5, "capped-fps"),
        (1u << 1, "vsync-off"),
        (1u << 4, "speed-frame")
    ];

    /// <summary>Who currently owns a physical input source.</summary>
    /// <remarks>
    ///     Ordered so the first value is the resting state. <c>device</c> means the Claw's own firmware
    ///     still has it, <c>plugin</c> means this plugin acquired it, and <c>unavailable</c> covers both
    ///     a failed acquisition and a source this unit does not expose. A user reading the row needs
    ///     those to be distinguishable, which is exactly what the previous boolean threw away.
    /// </remarks>
    private static readonly string[] SourceOwnershipChoices = ["device", "plugin", "unavailable"];

    private void BuildCapabilitySurface()
    {
        if (_power is null || _chargeLimit is null || _fans is null || _telemetry is null
            || _lighting is null
            || _motion is null || _controller is null)
        {
            throw new InvalidOperationException("Services must exist before descriptors are built.");
        }

        IReadOnlyList<CapabilityDescriptor> descriptors =
        [
            IntegerDescriptor(CapabilityIds.PowerSustained, CapabilityRole.PowerSustainedLimit,
                    // PL1 shares PL2's ceiling: on the A2VM that is 37 W, not the 30 W it ships at.
                    DisplayKey.SustainedPowerLimit, Model.MinimumWatts, Model.MaximumWatts,
                    CapabilityUnit.Watt, true,
                    section: SectionIds.Power, category: CategoryIds.Limits, order: 0) with
                {
                    PowerPresets = Model.PowerPresets,
                    PairedPowerLimitId = CapabilityIds.PowerBoost,
                    Prominence = CapabilityProminence.Primary,
                    LayoutPair = new CapabilityLayoutPair(CapabilityIds.PowerBoost)
                },
            IntegerDescriptor(CapabilityIds.PowerBoost, CapabilityRole.PowerSlowLimit,
                DisplayKey.BoostPowerLimit, Model.MinimumWatts, Model.MaximumWatts, CapabilityUnit.Watt, true,
                section: SectionIds.Power, category: CategoryIds.Limits, order: 1),
            IntegerDescriptor(CapabilityIds.ChargeLimit, CapabilityRole.ChargeLimit,
                    DisplayKey.ChargeLimit,
                    ClawChargeLimitCapability.MinimumPercent,
                    ClawChargeLimitCapability.MaximumPercent,
                    CapabilityUnit.Percent,
                    true,
                    persistence: CapabilityPersistence.DevicePersistent,
                    section: SectionIds.Power,
                    category: CategoryIds.Charging) with
                {
                    // HC's BatteryBypassStep: the limit is 60, 80 or 100 percent.
                    Step = ClawChargeLimitCapability.StepPercent
                },
            ChoiceDescriptor(
                CapabilityIds.Scenario,
                CapabilityRole.ScenarioMode,
                DisplayKey.PerformanceProfile,
                ["comfort", "green", "eco", "user", "sport", "inactive"],
                true,
                SectionIds.Power),
            ChoiceDescriptor(
                CapabilityIds.FanMode,
                CapabilityRole.FanMode,
                DisplayKey.FanMode,
                ["automatic", "custom", "full-speed"],
                true,
                SectionIds.Power,
                CategoryIds.Control),
            FanCurveDescriptor(1),
            IntegerDescriptor(CapabilityIds.LightingBrightness, CapabilityRole.LightingBrightness,
                DisplayKey.Brightness, 0, 100, CapabilityUnit.Percent, true,
                persistence: CapabilityPersistence.DevicePersistent,
                section: SectionIds.Lighting),
            LightingColorDescriptor(CapabilityInstances.LeftRing, "Left ring", 0),
            LightingColorDescriptor(CapabilityInstances.RightRing, "Right ring", 1),
            LightingColorDescriptor(CapabilityInstances.Buttons, "Buttons", 2),
            // These three carry the value kinds their roles require. ControllerSource and
            // MotionSource are choices because "who owns this source" has more than two answers:
            // the plugin can hold it, the device can still have it, or acquisition can have failed,
            // and a boolean flattened all three into "not owned". HapticSink carries no value at
            // all: it is a target rumble is written to, not something with a readable state.
            //
            // Declaring them as booleans made the descriptor set fail the SDK's role/value-kind
            // check, and a rejected SET means every capability is rejected, so the whole device
            // published nothing at all because of these three lines.
            ChoiceDescriptor(
                CapabilityIds.Controller,
                CapabilityRole.ControllerSource,
                DisplayKey.Controller,
                SourceOwnershipChoices,
                false,
                SectionIds.Info,
                CategoryIds.Ownership),
            ChoiceDescriptor(
                CapabilityIds.Motion,
                CapabilityRole.MotionSource,
                DisplayKey.Motion,
                SourceOwnershipChoices,
                false,
                SectionIds.Info,
                CategoryIds.Ownership,
                1),
            ActionDescriptor(
                CapabilityIds.Rumble,
                CapabilityRole.HapticSink,
                DisplayKey.Rumble,
                SectionIds.Info,
                CategoryIds.Ownership,
                2),
            // Readings, not controls. They left the Power page because a person opens it to change
            // how the device behaves, not to watch numbers; the CPU temperature stays published
            // because the fan-curve editor draws the live temperature against the curve.
            IntegerDescriptor(CapabilityIds.Temperature, CapabilityRole.Telemetry,
                DisplayKey.CpuTemperature, 0, 110, CapabilityUnit.Celsius, false,
                section: SectionIds.Info, category: CategoryIds.Readings, order: 0),
            // Both fans are still measured separately even though they are driven together: one
            // failing fan is exactly the fault this page exists to make visible.
            IntegerDescriptor(CapabilityIds.FanRpm, CapabilityRole.FanMeasuredRpm,
                DisplayKey.FanLeft, 0, 10_000, CapabilityUnit.Rpm, false,
                CapabilityInstances.Left,
                section: SectionIds.Info, category: CategoryIds.Readings, order: 1),
            IntegerDescriptor(CapabilityIds.FanRpm, CapabilityRole.FanMeasuredRpm,
                DisplayKey.FanRight, 0, 10_000, CapabilityUnit.Rpm, false,
                CapabilityInstances.Right,
                section: SectionIds.Info, category: CategoryIds.Readings, order: 2),
            .. _arcSync?.IsAvailable == true
                ?
                [
                    // Published only when a variable-refresh capable panel actually answered. A
                    // descriptor for a panel that cannot do it would draw a row that always
                    // refuses, which is worse than no row: the device-persistent marking is
                    // deliberate, because the driver keeps the profile across a WSGM restart.
                    //
                    // It sits under Power rather than in a Display section of its own: one toggle
                    // does not earn a page, and variable refresh is a decision about how the device
                    // performs, which is what Power now holds end to end.
                    BooleanDescriptor(
                            CapabilityIds.VariableRefreshRate,
                            CapabilityRole.VariableRefreshRate,
                            DisplayKey.VariableRefreshRate,
                            true,
                            SectionIds.Power) with
                        {
                            Persistence = CapabilityPersistence.DevicePersistent
                        }
                ]
                : (IReadOnlyList<CapabilityDescriptor>)[],
            .. _arcSync?.IsEnduranceGamingAvailable == true
                ?
                [
                    // Published only when the driver answered for the feature, for the same reason
                    // variable refresh is: a row that always refuses is worse than no row. Also
                    // device-persistent: the driver keeps this across a WSGM restart, and the
                    // Restore path only puts back what it captured at acquire.
                    ChoiceDescriptor(
                            CapabilityIds.EnduranceGaming,
                            CapabilityRole.GenericChoice,
                            DisplayKey.Custom,
                            ["off", "on", "auto"],
                            true,
                            SectionIds.Power) with
                        {
                            Display = new CapabilityDisplay
                            {
                                Key = DisplayKey.Custom,
                                CustomLabel = "Endurance Gaming"
                            },
                            Persistence = CapabilityPersistence.DevicePersistent
                        },
                    ChoiceDescriptor(
                            CapabilityIds.EnduranceGamingMode,
                            CapabilityRole.GenericChoice,
                            DisplayKey.Custom,
                            ["performance", "balanced", "battery"],
                            true,
                            SectionIds.Power) with
                        {
                            Display = new CapabilityDisplay
                            {
                                Key = DisplayKey.Custom,
                                CustomLabel = "Endurance Gaming target"
                            },
                            Persistence = CapabilityPersistence.DevicePersistent
                        }
                ]
                : (IReadOnlyList<CapabilityDescriptor>)[],
            .. _arcSync?.IsShaderDownloadAvailable == true
                ?
                [
                    BooleanDescriptor(
                            CapabilityIds.ShaderDownload,
                            CapabilityRole.GenericToggle,
                            DisplayKey.Custom,
                            true,
                            SectionIds.Power) with
                        {
                            Display = new CapabilityDisplay
                            {
                                Key = DisplayKey.Custom,
                                CustomLabel = "Download prebuilt shaders"
                            },
                            Persistence = CapabilityPersistence.DevicePersistent
                        }
                ]
                : (IReadOnlyList<CapabilityDescriptor>)[],
            .. FlipModeChoices() is { Length: > 1 } flipChoices
                ?
                [
                    // A choice, not a toggle. Intel has no VSync boolean: it has a presentation
                    // mode whose members include forcing sync on, Smooth Sync and a capped-FPS
                    // mode, and the offered set is whatever this driver reports it supports, so a
                    // future driver that adds one gets it without a contract change, which is the
                    // forward-compatibility this capability was asked for.
                    ChoiceDescriptor(
                            CapabilityIds.DriverVsync,
                            CapabilityRole.GenericChoice,
                            DisplayKey.Custom,
                            flipChoices,
                            true,
                            SectionIds.Power) with
                        {
                            Display = new CapabilityDisplay
                            {
                                Key = DisplayKey.Custom,
                                CustomLabel = "Frame presentation (restart)"
                            },
                            Persistence = CapabilityPersistence.DevicePersistent
                        }
                ]
                : (IReadOnlyList<CapabilityDescriptor>)[],
            .. _arcSync?.IsSharedGpuMemoryAvailable == true
                ?
                [
                    // The restart requirement is in the label because there is nowhere else for it
                    // to go: the SDK has no "takes effect later" field, and a row that appears to do
                    // nothing until the next boot is exactly the silent control the guidance forbids.
                    IntegerDescriptor(
                            CapabilityIds.SharedGpuMemory,
                            CapabilityRole.GenericRange,
                            DisplayKey.Custom,
                            IntelGraphicsMemoryTransport.MinimumPercent,
                            IntelGraphicsMemoryTransport.MaximumPercent,
                            CapabilityUnit.Percent,
                            true,
                            persistence: CapabilityPersistence.DevicePersistent,
                            section: SectionIds.Power) with
                        {
                            Display = new CapabilityDisplay
                            {
                                Key = DisplayKey.Custom,
                                CustomLabel = "GPU memory share (restart)"
                            }
                        }
                ]
                : (IReadOnlyList<CapabilityDescriptor>)[]
        ];

        EnsureUniqueCapabilityKeys(descriptors);
        _descriptorSet = new CapabilityDescriptorSet
        {
            Generation = 1,
            CycleGeneration = _cycleGeneration,
            Sections = OverlaySections,
            Descriptors = descriptors
        };
    }

    private static void EnsureUniqueCapabilityKeys(IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            var key = CapabilityKey(
                descriptor.CapabilityId,
                descriptor.InstanceId);
            if (!keys.Add(key))
            {
                throw new InvalidOperationException($"Capability '{key}' is registered more than once.");
            }
        }
    }

    private static string CapabilityKey(string capabilityId, string? instanceId)
    {
        return instanceId is null ? capabilityId : $"{capabilityId}/{instanceId}";
    }

    /// <summary>The one fan curve, applied to both channels.</summary>
    /// <remarks>
    ///     One capability rather than a left and a right instance. The A2VM's fans share a heatsink and
    ///     the firmware ramps them together, so two independently authored curves described a machine
    ///     that does not exist and made the user set the same thing twice.
    ///     <para>
    ///         The 0-100 bounds are declared, not implied: they are what the firmware accepts for a duty
    ///         byte, and WSGM's curve editor needs a stated range to draw an axis and clamp a drag against.
    ///         An undeclared bound means "no limit" to the router, which would let the editor offer values
    ///         the write would then refuse.
    ///     </para>
    /// </remarks>
    private static CapabilityDescriptor FanCurveDescriptor(int order)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = CapabilityIds.FanCurve,
            Role = CapabilityRole.FanCurve,
            SectionId = SectionIds.Power,
            CategoryId = CategoryIds.Control,
            SortOrder = order,
            ValueKind = CapabilityValueKind.Curve,
            Display = new CapabilityDisplay { Key = DisplayKey.FanCurve },
            Minimum = 0,
            Maximum = 100,
            Unit = CapabilityUnit.Percent,
            SupportsRead = true,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile
        };
    }

    private static CapabilityDescriptor LightingColorDescriptor(
        string instance,
        string label,
        int order)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = CapabilityIds.LightingColor,
            InstanceId = instance,
            Role = CapabilityRole.LightingZoneColor,
            SectionId = SectionIds.Lighting,
            CategoryId = CategoryIds.Zones,
            SortOrder = order,
            ValueKind = CapabilityValueKind.Color,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = label },
            SupportsRead = true,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.DevicePersistent
        };
    }

    private async ValueTask PublishCapabilityStatesAsync(CancellationToken cancellationToken)
    {
        if (_host is null || _descriptorSet is null)
        {
            return;
        }

        foreach (var descriptor in _descriptorSet.Descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var service = ServiceForCapability(descriptor.CapabilityId);
            if (service is null)
            {
                continue;
            }

            // A descriptor that cannot be read has no observed value to publish. The haptic sink is
            // the only one: rumble is written to it and never read back, so a state carrying a value
            // for it is rejected against its own descriptor shape. Its availability still matters,
            // so the state is published with no value, which is what "not readable" means.
            var failed = _observationFailures.TryGetValue(service.ServiceId, out var observationFailure);
            var value = descriptor.SupportsRead && !failed ? CurrentState(descriptor) : null;
            await _host.PublishCapabilityStateAsync(
                new CapabilityState
                {
                    CapabilityId = descriptor.CapabilityId,
                    InstanceId = descriptor.InstanceId,
                    Available = service.State is DeviceServiceState.Owned,
                    Reason = observationFailure ?? service.Reason ?? DeviceServiceLifecycle.ReasonFor(service.State),
                    ObservedValue = value,
                    Quality = value is null
                        ? HardwareStateQuality.Unknown
                        : HardwareStateQuality.Observed,
                    ObservedAt = value is null ? null : DateTimeOffset.UtcNow,
                    DescriptorGeneration = _descriptorSet.Generation,
                    CycleGeneration = _cycleGeneration
                },
                cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private CapabilityValue? CurrentState(CapabilityDescriptor descriptor)
    {
        switch (descriptor.CapabilityId)
        {
            case CapabilityIds.PowerSustained:
            {
                return _power!.LastObserved is { } value ? CapabilityValue.Integer(value.SustainedWatts) : null;
            }
            case CapabilityIds.PowerBoost:
            {
                return _power!.LastObserved is { } value ? CapabilityValue.Integer(value.BoostWatts) : null;
            }
            case CapabilityIds.Scenario:
            {
                return _power!.LastObserved is { } value ? Scenario(value.Scenario, Model) : null;
            }
            case CapabilityIds.ChargeLimit:
            {
                // A percentage outside the declared 60-100 bounds (0 after a BIOS reset on the
                // reference unit) is published as unknown rather than as a value the slider cannot
                // show; the capability stays available so the configured limit is written over it.
                return _chargeLimit!.LastObserved is
                {
                    Percent: >= ClawChargeLimitCapability.MinimumPercent
                    and <= ClawChargeLimitCapability.MaximumPercent
                } value
                    ? CapabilityValue.Integer(value.Percent)
                    : null;
            }
            case CapabilityIds.FanMode:
            {
                return _fans!.LastObserved is { } value ? FanMode(value) : null;
            }
            case CapabilityIds.FanCurve:
            {
                // The left channel stands for both. Every write installs one curve on the pair, so the
                // two tables can only disagree if something outside WSGM wrote one of them, and the
                // next write puts them back together.
                var value = _fans!.LastObserved;
                return value is null ? null : CapabilityValue.Curve(ClawFanCapability.DecodeCurve(value.Left));
            }
            case CapabilityIds.FanRpm:
            {
                var value = _telemetry!.LastTelemetry;
                return value is null
                    ? null
                    : CapabilityValue.Integer(
                        descriptor.InstanceId == CapabilityInstances.Left ? value.LeftRpm : value.RightRpm);
            }
            case CapabilityIds.Temperature:
            {
                return _telemetry!.LastTelemetry is { } value
                    ? CapabilityValue.Integer(value.TemperatureCelsius)
                    : null;
            }
            case CapabilityIds.LightingBrightness:
            {
                return _lighting!.LastObserved is { } value ? CapabilityValue.Integer(value.Brightness) : null;
            }
            case CapabilityIds.LightingColor:
            {
                var value = _lighting!.LastObserved;
                return value is null
                    ? null
                    : CapabilityValue.Color(descriptor.InstanceId switch
                    {
                        CapabilityInstances.RightRing => value.RightRingColor,
                        CapabilityInstances.LeftRing => value.LeftRingColor,
                        CapabilityInstances.Buttons => value.ButtonsColor,
                        _ => 0
                    });
            }
            case CapabilityIds.Rumble:
                // A sink has no value to report. Its descriptor says so, and its state has to agree or
                // the state is rejected for a kind mismatch the way the descriptor set was.
                return CapabilityValue.None();
            case CapabilityIds.VariableRefreshRate:
            {
                // This branch was missing, and its absence was invisible until it wasn't: VRR fell
                // through to the controller-ownership Choice below, publishing a Choice value against a
                // Boolean descriptor. The router rejected every VRR state for the kind mismatch, the
                // capability never became available, and Valve's own VRR row, which hides itself
                // through exactly that availability, never rendered. One log line every ten seconds
                // said all of this; it took a missing row to make anyone read it.
                var state = _arcSync?.Read();
                return state is { Supported: true } observed ? CapabilityValue.Boolean(observed.Enabled) : null;
            }
            case CapabilityIds.EnduranceGaming:
            {
                return _arcSync?.ReadEnduranceGaming() is { } endurance
                    ? CapabilityValue.Choice(endurance.Control switch
                    {
                        EnduranceGamingControl.On => "on",
                        EnduranceGamingControl.Auto => "auto",
                        _ => "off"
                    })
                    : null;
            }
            case CapabilityIds.ShaderDownload:
            {
                return _arcSync?.ReadShaderDownload() is { } shader ? CapabilityValue.Boolean(shader) : null;
            }
            case CapabilityIds.DriverVsync:
            {
                // An adapter that stores nothing is at Intel's default, which is application default.
                var stored = _arcSync?.ReadFlipMode() ?? 0;
                var match = Array.Find(FlipModes, mode => mode.Bit == stored);
                return CapabilityValue.Choice(match.Value ?? "application-default");
            }
            case CapabilityIds.SharedGpuMemory:
            {
                // The stored percentage, not the size the driver currently reports. Those two disagree
                // between a write and the next restart, and the row has to show what was asked for.
                return _arcSync?.ReadSharedGpuMemory() is { } memory ? CapabilityValue.Integer(memory.Percent) : null;
            }
            case CapabilityIds.EnduranceGamingMode:
            {
                return _arcSync?.ReadEnduranceGaming() is { } target
                    ? CapabilityValue.Choice(target.Mode switch
                    {
                        EnduranceGamingMode.Balanced => "balanced",
                        EnduranceGamingMode.Battery => "battery",
                        _ => "performance"
                    })
                    : null;
            }
        }

        return CapabilityValue.Choice(DeviceServiceLifecycle.Ownership(
            descriptor.CapabilityId == CapabilityIds.Motion ? _motion!.State : _controller!.State));
    }

    private static IReadOnlyList<OemControlDescriptor> CreateOemControls()
    {
        return
        [
            Oem("oem1", "Claw button", OemControlPlacement.Front, false,
                false),
            Oem("oem2", "Quick Settings", OemControlPlacement.Front, true,
                false),
            Oem("oem3", "M1", OemControlPlacement.Rear, false,
                true),
            Oem("oem4", "M2", OemControlPlacement.Rear, false,
                true)
        ];
    }

    private static OemControlDescriptor Oem(
        string id,
        string label,
        OemControlPlacement placement,
        bool supportsLongPress,
        bool requiresController)
    {
        return new OemControlDescriptor
        {
            ControlId = id,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = label },
            Placement = placement,
            SupportsLongPress = supportsLongPress,
            RequiresControllerAcquisition = requiresController
        };
    }

    private static CapabilityDescriptor IntegerDescriptor(
        string id,
        CapabilityRole role,
        DisplayKey display,
        int minimum,
        int maximum,
        CapabilityUnit unit,
        bool writable,
        string? instance = null,
        CapabilityPersistence persistence = CapabilityPersistence.Volatile,
        string? section = null,
        string? category = null,
        int order = 0)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id,
            InstanceId = instance,
            Role = role,
            SectionId = section,
            CategoryId = category,
            SortOrder = order,
            Prominence = writable ? CapabilityProminence.Normal : CapabilityProminence.Compact,
            ValueKind = CapabilityValueKind.Integer,
            Display = new CapabilityDisplay { Key = display },
            SupportsRead = true,
            SupportsWrite = writable,
            Minimum = minimum,
            Maximum = maximum,
            Step = 1,
            Unit = unit,
            Persistence = persistence
        };
    }

    /// <summary>The flip-mode choices this driver actually offers.</summary>
    /// <returns>The stable choice values, or an empty list when the driver reports none.</returns>
    /// <remarks>
    ///     Fewer than two is not a control: a row offering one option can only ever refuse, which is why
    ///     the descriptor is not published at all in that case.
    /// </remarks>
    private string[] FlipModeChoices()
    {
        return _arcSync?.ReadSupportedFlipModes() is not { } mask
            ? []
            : [.. FlipModes.Where(mode => (mask & mode.Bit) != 0).Select(mode => mode.Value)];
    }

    private static CapabilityDescriptor ChoiceDescriptor(
        string id,
        CapabilityRole role,
        DisplayKey display,
        IReadOnlyList<string> choices,
        bool writable,
        string? section = null,
        string? category = null,
        int order = 0)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id,
            Role = role,
            SectionId = section,
            CategoryId = category,
            SortOrder = order,
            ValueKind = CapabilityValueKind.Choice,
            Display = new CapabilityDisplay { Key = display },
            SupportsRead = true,
            SupportsWrite = writable,
            Choices =
            [
                .. choices.Select(choice => new CapabilityChoice(
                    choice,
                    new CapabilityDisplay
                    {
                        Key = DisplayKey.Custom, CustomLabel = choice switch
                        {
                            "comfort" => "Comfort", "green" => "Green", "eco" => "Eco", "sport" => "Sport",
                            "user" => "User", "inactive" => "Inactive", "automatic" => "Automatic",
                            "custom" => "Custom", "full-speed" => "Full speed",
                            _ => choice
                        }
                    }))
            ],
            Persistence = CapabilityPersistence.Volatile
        };
    }

    /// <summary>A capability that is invoked rather than read or written.</summary>
    /// <param name="id">Capability id.</param>
    /// <param name="role">Semantic role, which must be one the SDK maps to <c>None</c>.</param>
    /// <param name="display">Display key.</param>
    /// <param name="section">Overlay section id the row is grouped under, or null for the default.</param>
    /// <param name="category">Category within that section, or null for its lead group.</param>
    /// <param name="order">Sort order within the section; lower sorts first.</param>
    /// <returns>The descriptor.</returns>
    private static CapabilityDescriptor ActionDescriptor(
        string id,
        CapabilityRole role,
        DisplayKey display,
        string? section = null,
        string? category = null,
        int order = 0)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id,
            Role = role,
            SectionId = section,
            CategoryId = category,
            SortOrder = order,
            ValueKind = CapabilityValueKind.None,
            Display = new CapabilityDisplay { Key = display },
            SupportsRead = false,
            SupportsWrite = false,
            // A descriptor has to offer at least one operation, and for a sink the operation is the
            // invoke: rumble is written to it, never read back from it.
            SupportsAction = true,
            Persistence = CapabilityPersistence.Volatile
        };
    }

    private static CapabilityDescriptor BooleanDescriptor(
        string id,
        CapabilityRole role,
        DisplayKey display,
        bool writable,
        string? section = null,
        int order = 0)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id,
            Role = role,
            SectionId = section,
            SortOrder = order,
            ValueKind = CapabilityValueKind.Boolean,
            Display = new CapabilityDisplay { Key = display },
            SupportsRead = true,
            SupportsWrite = writable,
            Persistence = CapabilityPersistence.Volatile
        };
    }

    internal static CapabilityValue Scenario(byte raw, ClawModel model)
    {
        var mode = raw & 0x3F;
        return CapabilityValue.Choice(
            (raw & 0xC0) != 0xC0
                ? "inactive"
                : mode == model.UserScenario
                    ? "user"
                    : mode switch
                    {
                        0 => "comfort",
                        1 => "green",
                        2 => "eco",
                        4 => "sport",
                        _ => "unknown"
                    });
    }

    private static CapabilityValue FanMode(FanSnapshot snapshot)
    {
        return CapabilityValue.Choice(
            (snapshot.CustomFlag & 0x80) != 0
                ? "custom"
                : (snapshot.FullSpeedFlag & 0x80) != 0
                    ? "full-speed"
                    : "automatic");
    }
}
