using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.IntelGpu.Controls;
using WSGM.Plugin.IntelGpu.Igcl;

namespace WSGM.Plugin.IntelGpu.Display;

/// <summary>Which power-saving setting a control publishes.</summary>
internal enum PowerField
{
    Enable,
    LowRefreshRate,
    DpstLevel
}

/// <summary>
///     The display power savings of <c>ctlGet/SetPowerOptimizationSetting</c>: panel self refresh,
///     frame buffer compression, low refresh rate and display power saving technology, each for the
///     power source it applies to.
/// </summary>
/// <remarks>
///     The driver keeps a value per power source, as Intel Graphics Software's plugged-in and on-battery
///     tabs show, so each setting is published once per source it has. DPST is a battery feature and
///     only has the on-battery row. Version 1 of the structures, as Intel's sample uses, answers per
///     display output.
/// </remarks>
internal sealed unsafe class PowerSavingControl : IntelControl
{
    public const uint FeatureFbc = 1 << 0;
    public const uint FeaturePsr = 1 << 1;
    public const uint FeatureDpst = 1 << 2;
    public const uint FeatureLrr = 1 << 3;
    public const uint FeatureLace = 1 << 4;
    private const int SourceAc = 0;
    private const int SourceDc = 1;
    private const int PlanBalanced = 0;
    private const uint DpstBacklight = 1 << 0;

    private static readonly (uint Flag, string Id, string Label)[] LrrTypes =
    [
        (1 << 0, "lrr-1", "LRR 1.0"),
        (1 << 1, "lrr-2", "LRR 2.0"),
        (1 << 2, "lrr-2.5", "LRR 2.5"),
        (1 << 3, "autonomous", "Autonomous"),
        (1 << 4, "user-low", "User-based low refresh"),
        (1 << 5, "user-zero", "User-based zero refresh")
    ];

    private readonly uint _feature;
    private readonly PowerField _field;
    private readonly IntelLog _log;
    private readonly IgclOutput _output;
    private readonly IgclSession _session;
    private readonly int _source;

    private PowerSavingControl(
        IgclSession session,
        IgclOutput output,
        uint feature,
        int source,
        PowerField field,
        IntelLog log,
        CapabilityDescriptor descriptor)
        : base(descriptor)
    {
        _session = session;
        _output = output;
        _feature = feature;
        _source = source;
        _field = field;
        _log = log;
    }

    /// <summary>The features this output supports, or zero.</summary>
    /// <param name="session">The session.</param>
    /// <param name="output">The output.</param>
    /// <returns>The <c>ctl_power_optimization_flag_t</c> mask.</returns>
    public static uint SupportedFeatures(IgclSession session, IgclOutput output)
    {
        if (session.Api.GetPowerCaps is null
            || session.Api.GetPowerSetting is null
            || session.Api.SetPowerSetting is null)
        {
            return 0;
        }

        CtlPowerOptimizationCaps caps = default;
        caps.Size = (uint)sizeof(CtlPowerOptimizationCaps);
        caps.Version = 1;
        return session.Observe(session.Api.GetPowerCaps(output.Handle, &caps)) == IgclResult.Success
            ? caps.SupportedFeatures
            : 0;
    }

    /// <summary>Builds the power-saving controls one output supports.</summary>
    /// <param name="session">The session.</param>
    /// <param name="output">The output.</param>
    /// <param name="supported">The supported feature mask.</param>
    /// <param name="instance">The display's instance id.</param>
    /// <param name="placement">Where they sit.</param>
    /// <param name="log">Receives the decisions.</param>
    /// <returns>The controls, possibly none.</returns>
    public static IReadOnlyList<IntelControl> Build(
        IgclSession session,
        IgclOutput output,
        uint supported,
        string instance,
        Placement placement,
        IntelLog log)
    {
        List<IntelControl> controls = [];
        var order = placement.Order;
        foreach (var (source, suffix, label) in new[] { (SourceAc, "plugged-in", "plugged in"), (SourceDc, "battery", "on battery") })
        {
            if ((supported & FeaturePsr) != 0 && Read(session, output, FeaturePsr, source, out _) == IgclResult.Success)
            {
                controls.Add(Create(FeaturePsr, source, PowerField.Enable,
                    Descriptors.Toggle($"display.psr.{suffix}", instance, $"Panel self refresh ({label})",
                        placement with { Order = order++ })));
            }

            if ((supported & FeatureFbc) != 0 && Read(session, output, FeatureFbc, source, out _) == IgclResult.Success)
            {
                controls.Add(Create(FeatureFbc, source, PowerField.Enable,
                    Descriptors.Toggle($"display.fbc.{suffix}", instance, $"Frame buffer compression ({label})",
                        placement with { Order = order++ })));
            }

            if ((supported & FeatureLrr) != 0
                && Read(session, output, FeatureLrr, source, out var lrr) == IgclResult.Success)
            {
                List<(string, string)> choices = [("off", "Off")];
                choices.AddRange(LrrTypes
                    .Where(type => (lrr.Data.Lrr.SupportedTypes & type.Flag) != 0)
                    .Select(type => (type.Id, type.Label)));
                if (choices.Count > 1)
                {
                    controls.Add(Create(FeatureLrr, source, PowerField.LowRefreshRate,
                        Descriptors.Choice($"display.lrr.{suffix}", instance, $"Low refresh rate ({label})", choices,
                            placement with { Order = order++ })));
                }
            }
        }

        if ((supported & FeatureDpst) != 0
            && Read(session, output, FeatureDpst, SourceDc, out var dpst) == IgclResult.Success)
        {
            controls.Add(Create(FeatureDpst, SourceDc, PowerField.Enable,
                Descriptors.Toggle("display.dpst", instance, "Display power saving (on battery)",
                    placement with { Order = order++ })));
            if (dpst.Data.Dpst.MaximumLevel > dpst.Data.Dpst.MinimumLevel)
            {
                controls.Add(Create(FeatureDpst, SourceDc, PowerField.DpstLevel,
                    Descriptors.Range("display.dpst-level", instance, "Display power saving level",
                        dpst.Data.Dpst.MinimumLevel, dpst.Data.Dpst.MaximumLevel, 1, CapabilityUnit.None,
                        placement with { Order = order })));
            }
        }

        return controls;

        PowerSavingControl Create(uint feature, int source, PowerField field, CapabilityDescriptor descriptor)
        {
            return new PowerSavingControl(session, output, feature, source, field, log, descriptor);
        }
    }

    /// <inheritdoc />
    public override ControlRead Read()
    {
        var result = Read(_session, _output, _feature, _source, out var settings);
        if (result != IgclResult.Success)
        {
            return ControlRead.Failed(result);
        }

        switch (_field)
        {
            case PowerField.LowRefreshRate:
            {
                if (settings.Enable == 0 || settings.Data.Lrr.CurrentTypes == 0)
                {
                    return ControlRead.Of(CapabilityValue.Choice("off"));
                }

                var match = Array.Find(LrrTypes, type => (settings.Data.Lrr.CurrentTypes & type.Flag) != 0);
                return match.Id is not null && Descriptor.Choices.Any(choice => choice.Value == match.Id)
                    ? ControlRead.Of(CapabilityValue.Choice(match.Id))
                    : ControlRead.Failed(result);
            }
            case PowerField.DpstLevel:
                return ControlRead.Of(CapabilityValue.Integer(settings.Data.Dpst.Level));
            case PowerField.Enable:
            default:
                return ControlRead.Of(CapabilityValue.Boolean(settings.Enable != 0));
        }
    }

    /// <inheritdoc />
    public override ControlWrite Write(CapabilityValue value)
    {
        // The read only carries the feature-specific fields a write must keep, such as the PSR version
        // or the DPST features; it never decides whether to write.
        var read = Read(_session, _output, _feature, _source, out var current);
        var request = current;
        Prepare(ref request, _feature, _source);
        switch (_field)
        {
            case PowerField.LowRefreshRate:
                return WriteLowRefreshRate(request, value.ChoiceValue);
            case PowerField.DpstLevel:
                request.Enable = 1;
                request.Data.Dpst.Level = (byte)Math.Clamp(value.IntegerValue ?? 0, 0, 255);
                request.Data.Dpst.EnabledFeatures = DpstFeatures(current, read);
                break;
            case PowerField.Enable:
            default:
                request.Enable = value.BooleanValue == true ? (byte)1 : (byte)0;
                if (_feature == FeatureDpst)
                {
                    request.Data.Dpst.EnabledFeatures = DpstFeatures(current, read);
                }

                break;
        }

        return ControlWrite.From(Set(ref request), Descriptor.Display.CustomLabel ?? CapabilityId);
    }

    /// <remarks>
    ///     When the driver says an LRR change needs PSR off, PSR for the same source is turned off first
    ///     and turned back on afterwards if it was on, as the header describes. A failed PSR restore is
    ///     traced; it is not retried.
    /// </remarks>
    private ControlWrite WriteLowRefreshRate(CtlPowerOptimizationSettings request, string? choice)
    {
        var type = Array.Find(LrrTypes, entry => entry.Id == choice);
        var psrWasOn = false;
        CtlPowerOptimizationSettings psr = default;
        if (request.Data.Lrr.RequirePsrDisable != 0
            && Read(_session, _output, FeaturePsr, _source, out psr) == IgclResult.Success
            && psr.Enable != 0)
        {
            psrWasOn = true;
            var off = psr;
            Prepare(ref off, FeaturePsr, _source);
            off.Enable = 0;
            var disabled = Set(ref off);
            if (disabled != IgclResult.Success)
            {
                return ControlWrite.From(disabled, "turning panel self refresh off for a low refresh rate change");
            }
        }

        request.Enable = type.Id is null ? (byte)0 : (byte)1;
        request.Data.Lrr.CurrentTypes = type.Flag;
        var result = Set(ref request);
        if (psrWasOn)
        {
            var on = psr;
            Prepare(ref on, FeaturePsr, _source);
            on.Enable = 1;
            var restored = Set(ref on);
            if (restored != IgclResult.Success)
            {
                _log.Error("power", $"Panel self refresh could not be turned back on ({IgclResult.Describe(restored)}).");
            }
        }

        return ControlWrite.From(result, "the low refresh rate");
    }

    private static uint DpstFeatures(CtlPowerOptimizationSettings current, int read)
    {
        if (read == IgclResult.Success && current.Data.Dpst.EnabledFeatures != 0)
        {
            return current.Data.Dpst.EnabledFeatures;
        }

        // Intel's sample: the backlight bit has to be set to enable Intel DPST.
        var supported = read == IgclResult.Success ? current.Data.Dpst.SupportedFeatures : 0;
        if (supported == 0 || (supported & DpstBacklight) != 0)
        {
            return DpstBacklight;
        }

        return supported & (uint)-(int)supported;
    }

    private int Set(ref CtlPowerOptimizationSettings request)
    {
        fixed (CtlPowerOptimizationSettings* pointer = &request)
        {
            return _session.Observe(_session.Api.SetPowerSetting(_output.Handle, pointer));
        }
    }

    private static void Prepare(ref CtlPowerOptimizationSettings request, uint feature, int source)
    {
        request.Size = (uint)sizeof(CtlPowerOptimizationSettings);
        request.Version = 1;
        request.Plan = PlanBalanced;
        request.Feature = feature;
        request.PowerSource = source;
        switch (feature)
        {
            case FeatureLrr:
                request.Data.Lrr.Size = (uint)sizeof(CtlPowerOptimizationLrr);
                break;
            case FeaturePsr:
                request.Data.Psr.Size = (uint)sizeof(CtlPowerOptimizationPsr);
                break;
            case FeatureDpst:
                request.Data.Dpst.Size = (uint)sizeof(CtlPowerOptimizationDpst);
                break;
        }
    }

    private static int Read(
        IgclSession session,
        IgclOutput output,
        uint feature,
        int source,
        out CtlPowerOptimizationSettings settings)
    {
        CtlPowerOptimizationSettings request = default;
        Prepare(ref request, feature, source);
        var result = session.Observe(session.Api.GetPowerSetting(output.Handle, &request));
        settings = request;
        return result;
    }
}

/// <summary>Lighting aware contrast enhancement: on or off, and its fixed strength.</summary>
internal sealed unsafe class LaceControl : IntelControl
{
    private const uint GetCurrent = 1 << 0;
    private const uint GetCapability = 1 << 2;
    private const int SetCustom = 1;
    private const uint TriggerAmbient = 1 << 0;
    private const uint TriggerFixed = 1 << 1;
    private const int MaxEntries = 64;
    private const int EntrySize = 8;
    private readonly bool _level;
    private readonly uint _tableEntries;
    private readonly IgclOutput _output;
    private readonly IgclSession _session;

    private LaceControl(
        IgclSession session,
        IgclOutput output,
        bool level,
        uint tableEntries,
        CapabilityDescriptor descriptor)
        : base(descriptor)
    {
        _session = session;
        _output = output;
        _level = level;
        _tableEntries = tableEntries;
    }

    /// <summary>Builds the LACE controls when the output supports it.</summary>
    /// <param name="session">The session.</param>
    /// <param name="output">The output.</param>
    /// <param name="instance">The display's instance id.</param>
    /// <param name="placement">Where they sit.</param>
    /// <returns>The controls, possibly none.</returns>
    public static IReadOnlyList<IntelControl> Build(
        IgclSession session,
        IgclOutput output,
        string instance,
        Placement placement)
    {
        if (session.Api.GetLace is null || session.Api.SetLace is null)
        {
            return [];
        }

        CtlLaceConfig caps = default;
        caps.Size = (uint)sizeof(CtlLaceConfig);
        caps.Version = 1;
        caps.OperationGet = GetCapability;
        if (session.Observe(session.Api.GetLace(output.Handle, &caps)) != IgclResult.Success)
        {
            return [];
        }

        var entries = Math.Min(caps.Aggressiveness.MaxEntries, MaxEntries);
        return
        [
            new LaceControl(session, output, false, entries,
                Descriptors.Toggle("display.lace", instance, "Contrast enhancement (LACE)", placement)),
            new LaceControl(session, output, true, entries,
                Descriptors.Range("display.lace-level", instance, "Contrast enhancement strength", 0, 100, 1,
                    CapabilityUnit.Percent, placement with { Order = placement.Order + 1 }))
        ];
    }

    /// <inheritdoc />
    public override ControlRead Read()
    {
        var table = stackalloc byte[MaxEntries * EntrySize];
        var result = Get(table, out var config);
        if (result != IgclResult.Success)
        {
            return ControlRead.Failed(result);
        }

        if (!_level)
        {
            return ControlRead.Of(CapabilityValue.Boolean(config.Enabled != 0));
        }

        if ((config.Trigger & TriggerFixed) == 0)
        {
            return new ControlRead(null, result, false,
                new CapabilityReason(CapabilityReasonCode.Unsupported, "LACE follows the ambient light sensor."));
        }

        return ControlRead.Of(CapabilityValue.Integer(Math.Min((int)config.Aggressiveness.FixedLevelPercent, 100)));
    }

    /// <inheritdoc />
    public override ControlWrite Write(CapabilityValue value)
    {
        var table = stackalloc byte[MaxEntries * EntrySize];
        var read = Get(table, out var current);
        CtlLaceConfig request = default;
        request.Size = (uint)sizeof(CtlLaceConfig);
        request.Version = 1;
        request.OperationSet = SetCustom;
        if (_level || read != IgclResult.Success || (current.Trigger & TriggerAmbient) == 0)
        {
            request.Trigger = TriggerFixed;
            request.Aggressiveness.FixedLevelPercent = _level
                ? (byte)Math.Clamp(value.IntegerValue ?? 0, 0, 100)
                : read == IgclResult.Success && (current.Trigger & TriggerFixed) != 0
                    ? current.Aggressiveness.FixedLevelPercent
                    : (byte)50;
            request.Enabled = _level ? (byte)1 : value.BooleanValue == true ? (byte)1 : (byte)0;
        }
        else
        {
            // Ambient mode carries its lux table; hand back exactly what the driver reported.
            request.Trigger = current.Trigger;
            request.Aggressiveness.Entries = Math.Min(current.Aggressiveness.Entries, _tableEntries);
            request.Aggressiveness.Table = (nint)table;
            request.Enabled = value.BooleanValue == true ? (byte)1 : (byte)0;
        }

        return ControlWrite.From(_session.Observe(_session.Api.SetLace(_output.Handle, &request)),
            "contrast enhancement");
    }

    private int Get(byte* table, out CtlLaceConfig config)
    {
        CtlLaceConfig request = default;
        request.Size = (uint)sizeof(CtlLaceConfig);
        request.Version = 1;
        request.OperationGet = GetCurrent;

        // Prepared for ambient mode, whose lux table the driver copies into the caller's buffer. In
        // fixed mode the driver overwrites the first byte with the level instead.
        request.Aggressiveness.Entries = _tableEntries;
        request.Aggressiveness.Table = _tableEntries == 0 ? 0 : (nint)table;
        var result = _session.Observe(_session.Api.GetLace(_output.Handle, &request));
        config = request;
        return result;
    }
}
