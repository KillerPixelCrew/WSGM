using System.Runtime.InteropServices;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.IntelGpu.Controls;
using WSGM.Plugin.IntelGpu.Igcl;

namespace WSGM.Plugin.IntelGpu.Display;

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
internal static unsafe class PowerSavingControls
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

    private static readonly EnumMember Off = new(0, "off", "Off");

    private static readonly EnumMember[] LrrTypes =
    [
        new(1 << 0, "lrr-1", "LRR 1.0"),
        new(1 << 1, "lrr-2", "LRR 2.0"),
        new(1 << 2, "lrr-2.5", "LRR 2.5"),
        new(1 << 3, "autonomous", "Autonomous"),
        new(1 << 4, "user-low", "User-based low refresh"),
        new(1 << 5, "user-zero", "User-based zero refresh")
    ];

    /// <summary>The on/off features published per power source, in offer order.</summary>
    private static readonly (uint Feature, string Id, string Label)[] Toggles =
    [
        (FeaturePsr, "psr", "Panel self refresh"),
        (FeatureFbc, "fbc", "Frame buffer compression")
    ];

    /// <summary>The features this output supports, or zero.</summary>
    /// <param name="session">The session.</param>
    /// <param name="output">The output.</param>
    /// <returns>The <c>ctl_power_optimization_flag_t</c> mask.</returns>
    public static uint SupportedFeatures(IgclSession session, IgclOutput output)
    {
        var api = session.Api;
        if (api.GetPowerCaps is null || api.GetPowerSetting is null || api.SetPowerSetting is null)
        {
            return 0;
        }

        CtlPowerOptimizationCaps caps = default;
        caps.Version = 1;
        return session.Call(api.GetPowerCaps, output.Handle, ref caps) == IgclResult.Success
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
        var order = 0;
        foreach (var (source, suffix, label) in new[]
                 {
                     (SourceAc, "plugged-in", "plugged in"),
                     (SourceDc, "battery", "on battery")
                 })
        {
            var psr = Source(session, output, FeaturePsr, source);
            foreach (var (feature, id, name) in Toggles)
            {
                var settings = feature == FeaturePsr ? psr : Source(session, output, feature, source);
                if ((supported & feature) != 0 && settings.Read(out _) == IgclResult.Success)
                {
                    controls.Add(Toggle(settings, feature, source,
                        Descriptors.Toggle($"display.{id}.{suffix}", instance, $"{name} ({label})",
                            placement.Plus(order++))));
                }
            }

            var lrr = Source(session, output, FeatureLrr, source);
            if ((supported & FeatureLrr) == 0 || lrr.Read(out var current) != IgclResult.Success)
            {
                continue;
            }

            IReadOnlyList<EnumMember> choices =
                [Off, .. EnumMembers.Supported(LrrTypes, current.Data.Lrr.SupportedTypes, EnumMaskKind.Flag)];
            if (choices.Count > 1)
            {
                controls.Add(new LowRefreshRateControl(lrr, psr, source, log,
                    Descriptors.Choice($"display.lrr.{suffix}", instance, $"Low refresh rate ({label})", choices,
                        placement.Plus(order++)),
                    choices));
            }
        }

        var dpst = Source(session, output, FeatureDpst, SourceDc);
        if ((supported & FeatureDpst) == 0 || dpst.Read(out var level) != IgclResult.Success)
        {
            return controls;
        }

        controls.Add(Toggle(dpst, FeatureDpst, SourceDc,
            Descriptors.Toggle("display.dpst", instance, "Display power saving (on battery)",
                placement.Plus(order++))));
        if (level.Data.Dpst.MaximumLevel > level.Data.Dpst.MinimumLevel)
        {
            var range = IntegerRange.Linear(level.Data.Dpst.MinimumLevel, level.Data.Dpst.MaximumLevel);
            controls.Add(new FieldControl<CtlPowerOptimizationSettings>(
                dpst,
                Descriptors.Range("display.dpst-level", instance, "Display power saving level", range,
                    CapabilityUnit.None, placement.Plus(order)),
                static (control, settings) =>
                    ControlRead.Of(CapabilityValue.Integer(control.Range!.Value.ToInteger(settings.Data.Dpst.Level))),
                static (_, current, known, value) =>
                {
                    var request = Prepared(current, FeatureDpst, SourceDc);
                    request.Enable = 1;
                    request.Data.Dpst.Level = (byte)value.IntegerValue!.Value;
                    request.Data.Dpst.EnabledFeatures = DpstFeatures(current, known);
                    return request;
                },
                true));
        }

        return controls;
    }

    /// <summary>The settings of one feature for one power source, as a get asks for them.</summary>
    private static IgclSource<CtlPowerOptimizationSettings> Source(
        IgclSession session,
        IgclOutput output,
        uint feature,
        int source)
    {
        return new IgclSource<CtlPowerOptimizationSettings>(session, output.Handle, session.Api.GetPowerSetting,
            session.Api.SetPowerSetting, Prepared(default, feature, source));
    }

    private static FieldControl<CtlPowerOptimizationSettings> Toggle(
        IgclSource<CtlPowerOptimizationSettings> settings,
        uint feature,
        int source,
        CapabilityDescriptor descriptor)
    {
        // The read only carries the feature-specific fields a write must keep, such as the PSR version
        // or the DPST features; it never decides whether to write.
        return new FieldControl<CtlPowerOptimizationSettings>(
            settings,
            descriptor,
            static (_, current) => ControlRead.Of(IntelControl.Boolean(current.Enable != 0)),
            (_, current, known, value) =>
            {
                var request = Prepared(current, feature, source);
                request.Enable = value.BooleanValue == true ? (byte)1 : (byte)0;
                if (feature == FeatureDpst)
                {
                    request.Data.Dpst.EnabledFeatures = DpstFeatures(current, known);
                }

                return request;
            },
            true);
    }

    private static uint DpstFeatures(CtlPowerOptimizationSettings current, bool known)
    {
        if (known && current.Data.Dpst.EnabledFeatures != 0)
        {
            return current.Data.Dpst.EnabledFeatures;
        }

        // Intel's sample: the backlight bit has to be set to enable Intel DPST.
        var supported = known ? current.Data.Dpst.SupportedFeatures : 0;
        if (supported == 0 || (supported & DpstBacklight) != 0)
        {
            return DpstBacklight;
        }

        return supported & (uint)-(int)supported;
    }

    /// <summary>A request for one feature and power source, keeping the other fields of a read.</summary>
    private static CtlPowerOptimizationSettings Prepared(CtlPowerOptimizationSettings settings, uint feature,
        int source)
    {
        var request = settings;
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

        return request;
    }

    /// <summary>The low refresh rate of one power source.</summary>
    /// <remarks>
    ///     When the driver says an LRR change needs PSR off, PSR for the same source is turned off first
    ///     and turned back on afterwards if it was on, as the header describes. A failed PSR restore is
    ///     traced; it is not retried.
    /// </remarks>
    private sealed class LowRefreshRateControl : FieldControl<CtlPowerOptimizationSettings>
    {
        private readonly IntelLog _log;
        private readonly IgclSource<CtlPowerOptimizationSettings> _psr;

        public LowRefreshRateControl(
            IgclSource<CtlPowerOptimizationSettings> lrr,
            IgclSource<CtlPowerOptimizationSettings> psr,
            int source,
            IntelLog log,
            CapabilityDescriptor descriptor,
            IReadOnlyList<EnumMember> members)
            : base(
                lrr,
                descriptor,
                static (control, settings) => ControlRead.Of(settings.Enable == 0 || settings.Data.Lrr.CurrentTypes == 0
                    ? Off.Choice
                    : EnumMembers.FirstFlag(control.Members, settings.Data.Lrr.CurrentTypes)?.Choice),
                (control, current, _, value) =>
                {
                    var type = control.ValueOf(value);
                    var request = Prepared(current, FeatureLrr, source);
                    request.Enable = type == 0 ? (byte)0 : (byte)1;
                    request.Data.Lrr.CurrentTypes = type;
                    return request;
                },
                true,
                members)
        {
            _psr = psr;
            _log = log;
        }

        /// <inheritdoc />
        protected override ControlWrite WriteValidated(CapabilityValue value)
        {
            var request = Encode(value);
            CtlPowerOptimizationSettings psr = default;
            var psrWasOn = request.Data.Lrr.RequirePsrDisable != 0
                           && _psr.Read(out psr) == IgclResult.Success
                           && psr.Enable != 0;
            if (psrWasOn)
            {
                var off = Prepared(psr, FeaturePsr, request.PowerSource);
                off.Enable = 0;
                var disabled = _psr.Write(off);
                if (disabled != IgclResult.Success)
                {
                    return ControlWrite.From(disabled, "turning panel self refresh off for a low refresh rate change");
                }
            }

            var result = Source.Write(request);
            if (psrWasOn)
            {
                var on = Prepared(psr, FeaturePsr, request.PowerSource);
                on.Enable = 1;
                var restored = _psr.Write(on);
                if (restored != IgclResult.Success)
                {
                    _log.Error("power",
                        $"Panel self refresh could not be turned back on ({IgclResult.Describe(restored)}).");
                }
            }

            return ControlWrite.From(result, "the low refresh rate");
        }
    }
}

/// <summary>Lighting aware contrast enhancement: on or off, and its fixed strength.</summary>
internal static unsafe class LaceControls
{
    private const uint GetCurrent = 1 << 0;
    private const uint GetCapability = 1 << 2;
    private const int SetCustom = 1;
    private const uint TriggerAmbient = 1 << 0;
    private const uint TriggerFixed = 1 << 1;
    private const int EntrySize = 8;

    /// <summary>The strength a switch from ambient to fixed mode starts at.</summary>
    private const byte DefaultLevel = 50;

    private static readonly CapabilityReason FollowsAmbientLight =
        new(CapabilityReasonCode.PrerequisiteMissing, "LACE follows the ambient light sensor.");

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
        var api = session.Api;
        if (api.GetLace is null || api.SetLace is null)
        {
            return [];
        }

        CtlLaceConfig caps = default;
        caps.Version = 1;
        caps.OperationGet = GetCapability;
        if (session.Call(api.GetLace, output.Handle, ref caps) != IgclResult.Success)
        {
            return [];
        }

        // Prepared for ambient mode, whose lux table the driver copies into the caller's buffer. In fixed
        // mode the driver overwrites the first byte with the level instead. The buffer lives on the pinned
        // heap, so its address holds for the life of the controls.
        var entries = caps.Aggressiveness.MaxEntries;
        var table = GC.AllocateArray<byte>((int)entries * EntrySize, true);
        var tableAddress = entries == 0 ? 0 : Marshal.UnsafeAddrOfPinnedArrayElement(table, 0);
        CtlLaceConfig request = default;
        request.Version = 1;
        request.OperationGet = GetCurrent;
        request.Aggressiveness.Entries = entries;
        request.Aggressiveness.Table = tableAddress;
        IgclSource<CtlLaceConfig> source = new(session, output.Handle, api.GetLace, api.SetLace, request);
        return
        [
            new FieldControl<CtlLaceConfig>(
                source,
                Descriptors.Toggle("display.lace", instance, "Contrast enhancement (LACE)", placement),
                static (_, config) => ControlRead.Of(IntelControl.Boolean(config.Enabled != 0)),
                (_, current, known, value) =>
                {
                    // Holds the table for as long as the controls live; the driver only ever sees its address.
                    GC.KeepAlive(table);
                    var enabled = value.BooleanValue == true;
                    if (known && (current.Trigger & TriggerAmbient) != 0)
                    {
                        // Ambient mode carries its lux table; hand back exactly what the driver reported.
                        var ambient = Request(enabled, current.Trigger);
                        ambient.Aggressiveness.Entries = Math.Min(current.Aggressiveness.Entries, entries);
                        ambient.Aggressiveness.Table = tableAddress;
                        return ambient;
                    }

                    var level = known && (current.Trigger & TriggerFixed) != 0
                        ? current.Aggressiveness.FixedLevelPercent
                        : DefaultLevel;
                    return Fixed(enabled, level);
                },
                true),
            new FieldControl<CtlLaceConfig>(
                source,
                Descriptors.Range("display.lace-level", instance, "Contrast enhancement strength",
                    IntegerRange.Linear(0, 100), CapabilityUnit.Percent, placement.Plus(1)),
                static (control, config) => (config.Trigger & TriggerFixed) == 0
                    ? ControlRead.Unavailable(FollowsAmbientLight)
                    : ControlRead.Of(CapabilityValue.Integer(
                        control.Range!.Value.ToInteger(config.Aggressiveness.FixedLevelPercent))),
                static (_, _, _, value) => Fixed(true, (byte)value.IntegerValue!.Value),
                false)
        ];
    }

    private static CtlLaceConfig Request(bool enabled, uint trigger)
    {
        CtlLaceConfig request = default;
        request.Version = 1;
        request.OperationSet = SetCustom;
        request.Trigger = trigger;
        request.Enabled = enabled ? (byte)1 : (byte)0;
        return request;
    }

    private static CtlLaceConfig Fixed(bool enabled, byte level)
    {
        var request = Request(enabled, TriggerFixed);
        request.Aggressiveness.FixedLevelPercent = level;
        return request;
    }
}
