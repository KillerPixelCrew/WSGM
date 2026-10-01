using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>In-memory Graphics surface used only by the explicitly safe overlay-test mode.</summary>
/// <remarks>
///     Shaped like the Intel package's publication, one adapter section and one display section, and
///     run through the same projection as the real one, so --overlay-test shows every kind of graphics
///     row: a game override with Use global, a Global-only row applying after restart, a row applying
///     when a game next starts, the unpaired frame rate limit and an unavailable row with its reason.
///     Writes change only this object.
/// </remarks>
internal sealed class SimulatedGraphicsOverlaySource : IGraphicsOverlaySource
{
    private const string PluginId = "wsgm.gpu.intel";
    private const string Adapter = "pci-8086-7d45-0-2-0";
    private const string Display = "internal-edid-boe0b78-1a2b3c4d";

    private readonly Dictionary<string, CapabilityValue> _values = new(StringComparer.Ordinal)
    {
        ["graphics.low-latency"] = CapabilityValue.Choice("on"),
        ["graphics.frame-rate-limit"] = CapabilityValue.Integer(0),
        ["graphics.anisotropic"] = CapabilityValue.Choice("app"),
        ["graphics.cmaa"] = Flag(false),
        ["graphics.shared-memory"] = CapabilityValue.Integer(50),
        ["display.variable-refresh"] = Flag(false),
        ["display.arc-sync-profile"] = CapabilityValue.Choice("recommended"),
        ["display.sharpening-intensity"] = CapabilityValue.Integer(50),
        ["display.color-saturation"] = CapabilityValue.Integer(100)
    };

    // The running game's own value for low latency, cleared by Use global.
    private bool _gameOverride = true;

    public event Action? Changed;

    public GraphicsOverlaySnapshot Snapshot()
    {
        var key = ProfileSettingKey.GpuPublisher(PluginId);
        GpuPublisherView publisher = new(new PluginInstanceIdentity(PluginId, "default"), "Intel Graphics", key,
            PluginHealth.Ready, null);
        CapabilitySection[] sections =
        [
            new()
            {
                SectionId = "graphics", Key = SettingSectionKey.Custom, CustomTitle = "Graphics",
                CustomDescription = "Intel graphics driver settings", Icon = SectionIcon.Wrench, SortOrder = 0,
                Categories =
                [
                    Category("frame", "Frame delivery", 0), Category("quality", "Image quality", 1),
                    Category("driver", "Driver", 2)
                ]
            },
            new()
            {
                SectionId = "display-1a2b3c4d", Key = SettingSectionKey.Custom, CustomTitle = "Built-in display",
                CustomDescription = "Intel display settings", Icon = SectionIcon.Display, SortOrder = 1,
                Categories =
                [
                    Category("refresh", "Refresh", 0), Category("picture", "Picture", 1),
                    Category("color", "Colour", 2)
                ]
            }
        ];
        var vrrOn = _values["display.variable-refresh"].BooleanValue is true;
        GpuCapabilityView[] capabilities =
        [
            Row(Choice("graphics.low-latency", Adapter, "Low latency", "graphics", "frame", 0,
                    CapabilityProfileScope.NativePerApplication, CapabilityApplyTiming.NextApplicationStart,
                    ("off", "Off"), ("on", "On"), ("boost", "On + Boost")),
                _gameOverride ? CapabilityValue.Choice("boost") : null,
                _gameOverride ? key + ":graphics.low-latency#" + Adapter : null),
            Row(Range("graphics.frame-rate-limit", Adapter, "Frame rate limit", "graphics", "frame", 10, 0, 240, 1,
                CapabilityProfileScope.Switched, CapabilityApplyTiming.Immediate)),
            Row(Choice("graphics.anisotropic", Adapter, "Anisotropic filtering", "graphics", "quality", 20,
                CapabilityProfileScope.Switched, CapabilityApplyTiming.Immediate, ("app", "Application choice"),
                ("2x", "2x"), ("4x", "4x"), ("8x", "8x"), ("16x", "16x"))),
            Row(Toggle("graphics.cmaa", Adapter, "Conservative morphological anti-aliasing", "graphics", "quality",
                30, CapabilityProfileScope.Switched, CapabilityApplyTiming.NextApplicationStart)),
            Row(Range("graphics.shared-memory", Adapter, "Shared GPU memory", "graphics", "driver", 900, 13, 87, 1,
                    CapabilityProfileScope.GlobalOnly, CapabilityApplyTiming.SystemRestart) with
                {
                    Unit = CapabilityUnit.Percent
                }),
            Row(Toggle("display.variable-refresh", Display, "Variable refresh", "display-1a2b3c4d", "refresh", 0,
                    CapabilityProfileScope.Switched, CapabilityApplyTiming.Immediate) with
                {
                    Role = CapabilityRole.VariableRefreshRate,
                    Display = new CapabilityDisplay { Key = DisplayKey.VariableRefreshRate }
                }),
            Row(Choice("display.arc-sync-profile", Display, "Arc Sync profile", "display-1a2b3c4d", "refresh", 10,
                    CapabilityProfileScope.Switched, CapabilityApplyTiming.Immediate, ("recommended", "Recommended"),
                    ("excellent", "Excellent"), ("good", "Good"), ("compatible", "Compatible")),
                unavailable: vrrOn ? null : "Turn on variable refresh to choose a profile."),
            Row(Range("display.sharpening-intensity", Display, "Sharpening", "display-1a2b3c4d", "picture", 0, 0, 100,
                    1, CapabilityProfileScope.Switched, CapabilityApplyTiming.Immediate) with
                {
                    Unit = CapabilityUnit.Percent
                }),
            Row(Range("display.color-saturation", Display, "Saturation", "display-1a2b3c4d", "color", 0, 75, 125, 1,
                    CapabilityProfileScope.Switched, CapabilityApplyTiming.Immediate) with
                {
                    Unit = CapabilityUnit.Percent
                })
        ];
        return GraphicsOverlayBridge.Project([new GpuPublisherSnapshot(publisher, sections, capabilities)]);
    }

    public Task<CapabilityCommandResult?> WriteAsync(
        DeviceOverlayCapability capability,
        CapabilityValue? value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        if (value is null || !_values.ContainsKey(capability.CapabilityId) || !capability.CanInvoke)
        {
            return Task.FromResult<CapabilityCommandResult?>(null);
        }

        if (capability.CapabilityId == "graphics.low-latency" && _gameOverride)
        {
            // Saved for the running game, as the real coordinator does with a native per-application value.
            _gameOverride = false;
        }

        _values[capability.CapabilityId] = value;
        Changed?.Invoke();
        return Task.FromResult<CapabilityCommandResult?>(new CapabilityCommandResult
        {
            CommandId = Guid.NewGuid(),
            Outcome = CommandOutcome.AppliedVerified,
            CompletedAt = DateTimeOffset.UtcNow
        });
    }

    public Task<bool> UseGlobalAsync(string overrideId, CancellationToken cancellationToken = default)
    {
        var removed = _gameOverride;
        _gameOverride = false;
        Changed?.Invoke();
        return Task.FromResult(removed);
    }

    public void Dispose()
    {
        Changed = null;
    }

    private GpuCapabilityView Row(CapabilityDescriptor descriptor, CapabilityValue? game = null,
        string? overrideId = null, string? unavailable = null)
    {
        var observed = _values[descriptor.CapabilityId];
        return new GpuCapabilityView(new DeviceCapabilityView(
            descriptor,
            new CapabilityProjection
            {
                State = new CapabilityState
                {
                    CapabilityId = descriptor.CapabilityId,
                    InstanceId = descriptor.InstanceId,
                    Available = unavailable is null,
                    Reason = unavailable is null
                        ? null
                        : new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing, unavailable),
                    ObservedValue = observed,
                    Quality = HardwareStateQuality.Verified,
                    ObservedAt = DateTimeOffset.UtcNow,
                    DescriptorGeneration = 1,
                    CycleGeneration = 1
                },
                DesiredValue = game ?? observed,
                DesiredSource = game is null ? ProfileSource.Global : ProfileSource.Game,
                GlobalDesiredValue = observed,
                ProfileScope = descriptor.ProfileScope,
                ApplyTiming = descriptor.ApplyTiming
            },
            null) { Publisher = ProfileSettingKey.GpuPublisher(PluginId) }, overrideId);
    }

    private static CapabilityDescriptor Base(string id, string instance, string label, string section,
        string category, int order, CapabilityProfileScope scope, CapabilityApplyTiming timing)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id,
            InstanceId = instance,
            Role = CapabilityRole.GenericToggle,
            ValueKind = CapabilityValueKind.Boolean,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = label },
            SectionId = section,
            CategoryId = category,
            SortOrder = order,
            SupportsRead = true,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.DevicePersistent,
            ProfileScope = scope,
            ApplyTiming = timing
        };
    }

    private static CapabilityDescriptor Toggle(string id, string instance, string label, string section,
        string category, int order, CapabilityProfileScope scope, CapabilityApplyTiming timing)
    {
        return Base(id, instance, label, section, category, order, scope, timing);
    }

    private static CapabilityDescriptor Range(string id, string instance, string label, string section,
        string category, int order, int minimum, int maximum, int step, CapabilityProfileScope scope,
        CapabilityApplyTiming timing)
    {
        return Base(id, instance, label, section, category, order, scope, timing) with
        {
            Role = CapabilityRole.GenericRange,
            ValueKind = CapabilityValueKind.Integer,
            Minimum = minimum,
            Maximum = maximum,
            Step = step
        };
    }

    private static CapabilityDescriptor Choice(string id, string instance, string label, string section,
        string category, int order, CapabilityProfileScope scope, CapabilityApplyTiming timing,
        params (string Value, string Label)[] choices)
    {
        return Base(id, instance, label, section, category, order, scope, timing) with
        {
            Role = CapabilityRole.GenericChoice,
            ValueKind = CapabilityValueKind.Choice,
            Choices =
            [
                .. choices.Select(choice => new CapabilityChoice(choice.Value,
                    new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = choice.Label }))
            ]
        };
    }

    private static CapabilityCategory Category(string id, string title, int order)
    {
        return new CapabilityCategory
            { CategoryId = id, Key = SettingSectionKey.Custom, CustomTitle = title, SortOrder = order };
    }

    private static CapabilityValue Flag(bool value)
    {
        return new CapabilityValue { Kind = CapabilityValueKind.Boolean, BooleanValue = value };
    }
}
