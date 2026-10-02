using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.IntelGpu.Controls;
using WSGM.Plugin.IntelGpu.Igcl;

namespace WSGM.Plugin.IntelGpu.Display;

/// <summary>Display scaling: how a mode smaller than the panel is placed on it.</summary>
internal static unsafe class ScalingControls
{
    private const uint Identity = 1 << 0;
    private const uint Centered = 1 << 1;
    private const uint Stretched = 1 << 2;
    private const uint AspectRatio = 1 << 3;
    private const uint Custom = 1 << 4;

    /// <summary>
    ///     The smallest custom scale. IGCL documents 0-100 percent of the OS resolution with at most 11 %
    ///     of downscaling.
    /// </summary>
    private const int CustomMinimum = 89;

    /// <summary>The largest custom scale, the OS resolution itself.</summary>
    private const int CustomMaximum = 100;

    private static readonly EnumMember[] Types =
    [
        new(Identity, "display", "Let the display scale"),
        new(Centered, "centered", "Centre image"),
        new(Stretched, "stretched", "Stretch to fill"),
        new(AspectRatio, "aspect-ratio", "Keep aspect ratio"),
        new(Custom, "custom", "Custom")
    ];

    private static readonly CapabilityReason CustomNotSelected =
        new(CapabilityReasonCode.PrerequisiteMissing, "Custom scaling is not selected.");

    /// <summary>Builds the scaling choice and, when custom scaling is offered, its two sizes.</summary>
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
        if (api.GetScalingCaps is null || api.GetScaling is null || api.SetScaling is null)
        {
            return [];
        }

        CtlScalingCaps caps = default;
        if (session.Call(api.GetScalingCaps, output.Handle, ref caps) != IgclResult.Success)
        {
            return [];
        }

        var offered = EnumMembers.Supported(Types, caps.SupportedScaling, EnumMaskKind.Flag);
        if (offered.Count < 2)
        {
            return [];
        }

        IgclSource<CtlScalingSettings> source = new(session, output.Handle, api.GetScaling, api.SetScaling, default);
        List<IntelControl> controls =
        [
            new FieldControl<CtlScalingSettings>(
                source,
                Descriptors.Choice("display.scaling", instance, "Scaling", offered, placement),
                static (control, settings) =>
                    ControlRead.Of(control.MemberOf(settings.Enable == 0 ? Identity : settings.ScalingType)),
                static (control, current, _, value) =>
                {
                    var request = Request(control.ValueOf(value));
                    request.CustomScalingX = Axis(current.CustomScalingX);
                    request.CustomScalingY = Axis(current.CustomScalingY);
                    return request;
                },
                true,
                offered)
        ];
        if ((caps.SupportedScaling & Custom) == 0)
        {
            return controls;
        }

        var range = IntegerRange.Linear(CustomMinimum, CustomMaximum);
        controls.Add(new FieldControl<CtlScalingSettings>(
            source,
            Descriptors.Range("display.scaling-width", instance, "Custom scaling width", range,
                CapabilityUnit.Percent, placement.Plus(1)),
            static (control, settings) => CustomAxis(control, settings, settings.CustomScalingX),
            static (_, current, _, value) =>
            {
                var request = Request(Custom);
                request.CustomScalingX = (uint)value.IntegerValue!.Value;
                request.CustomScalingY = Axis(current.CustomScalingY);
                return request;
            },
            true));
        controls.Add(new FieldControl<CtlScalingSettings>(
            source,
            Descriptors.Range("display.scaling-height", instance, "Custom scaling height", range,
                CapabilityUnit.Percent, placement.Plus(2)),
            static (control, settings) => CustomAxis(control, settings, settings.CustomScalingY),
            static (_, current, _, value) =>
            {
                var request = Request(Custom);
                request.CustomScalingX = Axis(current.CustomScalingX);
                request.CustomScalingY = (uint)value.IntegerValue!.Value;
                return request;
            },
            true));
        return controls;
    }

    private static CtlScalingSettings Request(uint type)
    {
        CtlScalingSettings request = default;
        request.Enable = 1;
        request.ScalingType = type;
        return request;
    }

    /// <summary>A custom scale the driver reported, or the full size when it is outside the offered range.</summary>
    private static uint Axis(uint value)
    {
        return value is >= CustomMinimum and <= CustomMaximum ? value : CustomMaximum;
    }

    private static ControlRead CustomAxis(IntelControl control, CtlScalingSettings settings, uint value)
    {
        return settings.Enable == 0 || settings.ScalingType != Custom
            ? ControlRead.Unavailable(CustomNotSelected)
            : ControlRead.Of(CapabilityValue.Integer(control.Range!.Value.ToInteger(value)));
    }
}

/// <summary>Display sharpening: on or off, the filter, and its intensity.</summary>
internal static unsafe class SharpnessControls
{
    private const uint NonAdaptive = 1 << 0;
    private const uint Adaptive = 1 << 1;

    private static readonly EnumMember[] Filters =
    [
        new(NonAdaptive, "non-adaptive", "Non-adaptive"),
        new(Adaptive, "adaptive", "Adaptive")
    ];

    /// <summary>Builds the sharpness controls the driver reports for one display.</summary>
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
        if (api.GetSharpnessCaps is null || api.GetSharpness is null || api.SetSharpness is null)
        {
            return [];
        }

        CtlSharpnessCaps caps = default;
        if (session.Call(api.GetSharpnessCaps, output.Handle, ref caps) != IgclResult.Success
            || (caps.SupportedFilterFlags & (NonAdaptive | Adaptive)) == 0)
        {
            return [];
        }

        var count = (int)caps.NumFilterTypes;
        var filters = new CtlSharpnessFilterProperties[Math.Max(count, 1)];
        var result = IgclResult.Success;
        if (count > 0)
        {
            fixed (CtlSharpnessFilterProperties* buffer = filters)
            {
                caps.FilterProperties = (nint)buffer;
                result = session.Call(api.GetSharpnessCaps, output.Handle, ref caps);
            }
        }

        // A write with the filter unknown uses the adaptive one when offered, as Intel's software does.
        var supported = caps.SupportedFilterFlags;
        var fallback = (supported & Adaptive) != 0 ? Adaptive : NonAdaptive;
        IgclSource<CtlSharpnessSettings> source = new(session, output.Handle, api.GetSharpness, api.SetSharpness,
            default);
        List<IntelControl> controls =
        [
            new FieldControl<CtlSharpnessSettings>(
                source,
                Descriptors.Toggle("display.sharpening", instance, "Display sharpening", placement),
                static (_, settings) => ControlRead.Of(IntelControl.Boolean(settings.Enable != 0)),
                (_, current, known, value) =>
                {
                    var request = Carried(current, known, supported, fallback);
                    request.Enable = value.BooleanValue == true ? (byte)1 : (byte)0;
                    return request;
                },
                true)
        ];

        var offered = EnumMembers.Supported(Filters, supported, EnumMaskKind.Flag);
        if (offered.Count > 1)
        {
            controls.Add(new FieldControl<CtlSharpnessSettings>(
                source,
                Descriptors.Choice("display.sharpening-filter", instance, "Sharpening filter", offered,
                    placement.Plus(1)),
                static (control, settings) =>
                    ControlRead.Of(control.MemberOf((settings.FilterType & Adaptive) != 0 ? Adaptive : NonAdaptive)),
                (control, current, known, value) =>
                {
                    var request = Carried(current, known, supported, fallback);
                    request.FilterType = control.ValueOf(value);
                    return request;
                },
                true,
                offered));
        }

        if (result != IgclResult.Success || count == 0)
        {
            return controls;
        }

        // One range for every filter: the union of what each reports.
        var minimum = float.MaxValue;
        var maximum = float.MinValue;
        var step = float.MaxValue;
        for (var index = 0; index < count; index++)
        {
            if (filters[index].Maximum > filters[index].Minimum)
            {
                minimum = Math.Min(minimum, filters[index].Minimum);
                maximum = Math.Max(maximum, filters[index].Maximum);
                step = Math.Min(step, filters[index].Step);
            }
        }

        if (maximum > minimum && ValueMapping.FromFloat(minimum, maximum, step, false) is { } range)
        {
            controls.Add(new FieldControl<CtlSharpnessSettings>(
                source,
                Descriptors.Range("display.sharpening-intensity", instance, "Sharpening intensity", range,
                    CapabilityUnit.None, placement.Plus(2)),
                static (control, settings) =>
                    ControlRead.Of(CapabilityValue.Integer(control.Range!.Value.ToInteger(settings.Intensity))),
                (control, current, known, value) =>
                {
                    var request = Carried(current, known, supported, fallback);
                    request.Intensity = (float)control.Range!.Value.ToNative(value.IntegerValue!.Value);
                    return request;
                },
                true,
                range: range));
        }

        return controls;
    }

    /// <summary>The current settings a write keeps, with a filter the display offers.</summary>
    private static CtlSharpnessSettings Carried(
        CtlSharpnessSettings current,
        bool known,
        uint supported,
        uint fallback)
    {
        var request = current;
        if (!known || (request.FilterType & supported) == 0)
        {
            request.FilterType = fallback;
        }

        return request;
    }
}

/// <summary>The wire format: colour model and bits per colour on the link.</summary>
internal static unsafe class WireFormatControl
{
    private const int OperationGet = 0;
    private const int OperationSet = 1;

    /// <summary>The fixed length of <c>ctl_wireformat_detailed_config_t</c>'s supported array.</summary>
    private const int SupportedEntries = 4;

    private static readonly EnumMember[] Models =
    [
        new(0, "rgb", "RGB"),
        new(1, "ycbcr420", "YCbCr 4:2:0"),
        new(2, "ycbcr422", "YCbCr 4:2:2"),
        new(3, "ycbcr444", "YCbCr 4:4:4")
    ];

    private static readonly (uint Flag, int Bits)[] Depths = [(1 << 0, 6), (1 << 1, 8), (1 << 2, 10), (1 << 3, 12)];

    /// <summary>Builds the control when the driver reports more than one format.</summary>
    /// <param name="session">The session.</param>
    /// <param name="output">The output.</param>
    /// <param name="instance">The display's instance id.</param>
    /// <param name="placement">Where it sits.</param>
    /// <returns>The control, or null.</returns>
    /// <remarks>
    ///     A choice's driver value packs the colour model above the depth flag, so one table lookup finds
    ///     both on a read and on a write.
    /// </remarks>
    public static IntelControl? TryCreate(
        IgclSession session,
        IgclOutput output,
        string instance,
        Placement placement)
    {
        var api = session.Api;
        if (api.GetSetWireFormat is null)
        {
            return null;
        }

        CtlWireFormatConfig request = default;
        request.Operation = OperationGet;
        IgclSource<CtlWireFormatConfig> source = new(session, output.Handle, api.GetSetWireFormat,
            api.GetSetWireFormat, request, static current =>
            {
                current.Operation = OperationSet;
                return current;
            });
        if (source.Read(out var config) != IgclResult.Success)
        {
            return null;
        }

        List<EnumMember> choices = [];
        for (var index = 0; index < SupportedEntries; index++)
        {
            var entry = config.SupportedAt(index);
            if (entry.ColorModel < 0 || EnumMembers.ByValue(Models, (uint)entry.ColorModel) is not { } model)
            {
                continue;
            }

            foreach (var (flag, bits) in Depths)
            {
                var code = Code(model.Value, flag);
                if ((entry.ColorDepth & flag) != 0 && EnumMembers.ByValue(choices, code) is null)
                {
                    choices.Add(new EnumMember(code, $"{model.Id}-{bits}", $"{model.Label}, {bits}-bit"));
                }
            }
        }

        if (choices.Count < 2)
        {
            return null;
        }

        return new FieldControl<CtlWireFormatConfig>(
            source,
            Descriptors.Choice("display.wire-format", instance, "Colour format", choices, placement),
            static (control, current) => ControlRead.Of(CurrentCode(current.Current) is { } code
                ? control.MemberOf(code)
                : null),
            static (control, _, _, value) =>
            {
                var code = control.ValueOf(value);
                CtlWireFormatConfig set = default;
                set.Operation = OperationSet;
                set.Current.Size = (uint)sizeof(CtlWireFormat);
                set.Current.ColorModel = (int)(code >> 8);
                set.Current.ColorDepth = code & 0xff;
                return set;
            },
            false,
            choices);
    }

    private static uint Code(uint model, uint depthFlag)
    {
        return (model << 8) | depthFlag;
    }

    /// <summary>The code of the format in use: its model and its lowest depth flag.</summary>
    private static uint? CurrentCode(CtlWireFormat current)
    {
        if (current.ColorModel < 0)
        {
            return null;
        }

        foreach (var (flag, _) in Depths)
        {
            if ((current.ColorDepth & flag) != 0)
            {
                return Code((uint)current.ColorModel, flag);
            }
        }

        return null;
    }
}

/// <summary>
///     The end-display settings signalled through info frames: quantization range, content type,
///     low latency mode and source tone mapping. Only those the display reports as both supported and
///     controllable are offered.
/// </summary>
internal static unsafe class DisplaySettingControls
{
    private static readonly EnumMember[] QuantizationRanges =
    [
        new(0, "default", "Default"),
        new(1, "limited", "Limited range"),
        new(2, "full", "Full range")
    ];

    private static readonly EnumMember[] ContentTypes =
    [
        new(0, "default", "Default"),
        new(1, "off", "Off"),
        new(2, "desktop", "Desktop"),
        new(3, "media", "Media"),
        new(4, "gaming", "Gaming")
    ];

    private static readonly EnumMember[] Switches =
    [
        new(0, "default", "Default"),
        new(1, "off", "Off"),
        new(2, "on", "On")
    ];

    /// <summary>Every setting, in offer order, each with the flag that names it in the structure.</summary>
    private static readonly Row[] Rows =
    [
        new(1 << 3, "display.quantization-range", "Quantization range", QuantizationRanges,
            static settings => settings.QuantizationRange,
            static (ref settings, value) => settings.QuantizationRange = value),
        new(1 << 2, "display.content-type", "Content type", ContentTypes,
            static settings => settings.ContentType,
            static (ref settings, value) => settings.ContentType = value),
        new(1 << 0, "display.low-latency", "Auto low latency mode", Switches,
            static settings => settings.LowLatency,
            static (ref settings, value) => settings.LowLatency = value),
        new(1 << 1, "display.source-tone-mapping", "Source tone mapping", Switches,
            static settings => settings.SourceToneMapping,
            static (ref settings, value) => settings.SourceToneMapping = value)
    ];

    /// <summary>Builds the settings the display offers.</summary>
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
        if (api.GetSetDisplaySettings is null)
        {
            return [];
        }

        IgclSource<CtlDisplaySettings> source = new(session, output.Handle, api.GetSetDisplaySettings,
            api.GetSetDisplaySettings, default, static current =>
            {
                current.Set = 1;
                current.ValidFlags &= current.ControllableFlags;
                return current;
            });
        if (source.Read(out var settings) != IgclResult.Success)
        {
            return [];
        }

        var usable = settings.SupportedFlags & settings.ControllableFlags;
        List<IntelControl> controls = [];
        foreach (var row in Rows)
        {
            if ((usable & row.Flag) == 0)
            {
                continue;
            }

            controls.Add(new FieldControl<CtlDisplaySettings>(
                source,
                Descriptors.Choice(row.Id, instance, row.Label, row.Members, placement.Plus(controls.Count)),
                (control, current) => ControlRead.Of(row.Get(current) is var raw and >= 0
                    ? control.MemberOf((uint)raw)
                    : null),
                (control, _, _, value) =>
                {
                    CtlDisplaySettings request = default;
                    request.Set = 1;
                    request.ValidFlags = row.Flag;
                    row.Set(ref request, (int)control.ValueOf(value));
                    return request;
                },
                false,
                row.Members));
        }

        return controls;
    }

    private delegate void Setter(ref CtlDisplaySettings settings, int value);

    private sealed record Row(
        uint Flag,
        string Id,
        string Label,
        EnumMember[] Members,
        Func<CtlDisplaySettings, int> Get,
        Setter Set);
}
