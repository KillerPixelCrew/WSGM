using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.IntelGpu.Controls;
using WSGM.Plugin.IntelGpu.Igcl;

namespace WSGM.Plugin.IntelGpu.Display;

/// <summary>Display scaling: how a mode smaller than the panel is placed on it.</summary>
internal sealed unsafe class ScalingControl : IntelControl
{
    private const uint Identity = 1 << 0;
    private const uint Centered = 1 << 1;
    private const uint Stretched = 1 << 2;
    private const uint AspectRatio = 1 << 3;
    private const uint Custom = 1 << 4;

    private static readonly (uint Flag, string Id, string Label)[] Types =
    [
        (Identity, "display", "Let the display scale"),
        (Centered, "centered", "Centre image"),
        (Stretched, "stretched", "Stretch to fill"),
        (AspectRatio, "aspect-ratio", "Keep aspect ratio"),
        (Custom, "custom", "Custom")
    ];

    private readonly IgclOutput _output;
    private readonly IgclSession _session;

    private ScalingControl(IgclSession session, IgclOutput output, CapabilityDescriptor descriptor)
        : base(descriptor)
    {
        _session = session;
        _output = output;
    }

    /// <summary>Builds the scaling choice and, when custom scaling is offered, its two sizes.</summary>
    /// <param name="session">The session.</param>
    /// <param name="output">The output.</param>
    /// <param name="instance">The display's instance id.</param>
    /// <param name="placement">Where they sit.</param>
    /// <returns>The controls, possibly none.</returns>
    public static IEnumerable<IntelControl> Build(
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
        caps.Size = (uint)sizeof(CtlScalingCaps);
        if (session.Observe(api.GetScalingCaps(output.Handle, &caps)) != IgclResult.Success)
        {
            return [];
        }

        var supported = caps.SupportedScaling;
        var offered = Types.Where(type => (supported & type.Flag) != 0).ToArray();
        if (offered.Length < 2)
        {
            return [];
        }

        List<IntelControl> controls =
        [
            new ScalingControl(
                session,
                output,
                Descriptors.Choice("display.scaling", instance, "Scaling",
                    [.. offered.Select(type => (type.Id, type.Label))], placement))
        ];
        if ((supported & Custom) == 0)
        {
            return controls;
        }

        // IGCL documents 0-100 percent of the OS resolution with at most 11 % of downscaling, so the
        // offered range is 89-100.
        controls.Add(new CustomScalingControl(session, output, true, Descriptors.Range("display.scaling-width",
            instance, "Custom scaling width", 89, 100, 1, CapabilityUnit.Percent,
            placement with { Order = placement.Order + 1 })));
        controls.Add(new CustomScalingControl(session, output, false, Descriptors.Range("display.scaling-height",
            instance, "Custom scaling height", 89, 100, 1, CapabilityUnit.Percent,
            placement with { Order = placement.Order + 2 })));
        return controls;
    }

    /// <inheritdoc />
    public override ControlRead Read()
    {
        var result = ReadSettings(_session, _output, out var settings);
        if (result != IgclResult.Success)
        {
            return ControlRead.Failed(result);
        }

        var type = settings.Enable == 0 ? Identity : settings.ScalingType;
        var match = Array.Find(Types, entry => entry.Flag == type);
        return match.Id is not null && Descriptor.Choices.Any(choice => choice.Value == match.Id)
            ? ControlRead.Of(CapabilityValue.Choice(match.Id))
            : ControlRead.Failed(result);
    }

    /// <inheritdoc />
    public override ControlWrite Write(CapabilityValue value)
    {
        var match = Array.Find(Types, entry => entry.Id == value.ChoiceValue);
        _ = ReadSettings(_session, _output, out var current);
        CtlScalingSettings request = default;
        request.Size = (uint)sizeof(CtlScalingSettings);
        request.Enable = 1;
        request.ScalingType = match.Flag;
        request.CustomScalingX = current.CustomScalingX is >= 89 and <= 100 ? current.CustomScalingX : 100;
        request.CustomScalingY = current.CustomScalingY is >= 89 and <= 100 ? current.CustomScalingY : 100;
        return ControlWrite.From(_session.Observe(_session.Api.SetScaling(_output.Handle, &request)), "scaling");
    }

    internal static int ReadSettings(IgclSession session, IgclOutput output, out CtlScalingSettings settings)
    {
        CtlScalingSettings current = default;
        current.Size = (uint)sizeof(CtlScalingSettings);
        var result = session.Observe(session.Api.GetScaling(output.Handle, &current));
        settings = current;
        return result;
    }

    /// <summary>One axis of custom scaling, offered while custom scaling is selected.</summary>
    private sealed class CustomScalingControl : IntelControl
    {
        private readonly IgclOutput _output;
        private readonly IgclSession _session;
        private readonly bool _width;

        public CustomScalingControl(IgclSession session, IgclOutput output, bool width, CapabilityDescriptor descriptor)
            : base(descriptor)
        {
            _session = session;
            _output = output;
            _width = width;
        }

        /// <inheritdoc />
        public override ControlRead Read()
        {
            var result = ReadSettings(_session, _output, out var settings);
            if (result != IgclResult.Success)
            {
                return ControlRead.Failed(result);
            }

            if (settings.Enable == 0 || settings.ScalingType != Custom)
            {
                return new ControlRead(null, result, false,
                    new CapabilityReason(CapabilityReasonCode.Unsupported, "Custom scaling is not selected."));
            }

            var value = _width ? settings.CustomScalingX : settings.CustomScalingY;
            return ControlRead.Of(CapabilityValue.Integer((int)Math.Clamp(value, 89u, 100u)));
        }

        /// <inheritdoc />
        public override ControlWrite Write(CapabilityValue value)
        {
            _ = ReadSettings(_session, _output, out var current);
            CtlScalingSettings request = default;
            request.Size = (uint)sizeof(CtlScalingSettings);
            request.Enable = 1;
            request.ScalingType = Custom;
            var other = _width ? current.CustomScalingY : current.CustomScalingX;
            other = other is >= 89 and <= 100 ? other : 100;
            request.CustomScalingX = _width ? (uint)(value.IntegerValue ?? 100) : other;
            request.CustomScalingY = _width ? other : (uint)(value.IntegerValue ?? 100);
            return ControlWrite.From(_session.Observe(_session.Api.SetScaling(_output.Handle, &request)),
                "custom scaling");
        }
    }
}

/// <summary>Which part of the display sharpness setting a control publishes.</summary>
internal enum SharpnessField
{
    Enable,
    Filter,
    Intensity
}

/// <summary>Display sharpening: on or off, the filter, and its intensity.</summary>
internal sealed unsafe class SharpnessControl : IntelControl
{
    private const uint NonAdaptive = 1 << 0;
    private const uint Adaptive = 1 << 1;
    private readonly SharpnessField _field;
    private readonly IgclOutput _output;
    private readonly IntegerRange? _range;
    private readonly IgclSession _session;
    private readonly uint _supportedFilters;

    private SharpnessControl(
        IgclSession session,
        IgclOutput output,
        SharpnessField field,
        uint supportedFilters,
        IntegerRange? range,
        CapabilityDescriptor descriptor)
        : base(descriptor)
    {
        _session = session;
        _output = output;
        _field = field;
        _supportedFilters = supportedFilters;
        _range = range;
    }

    /// <summary>Builds the sharpness controls the driver reports for one display.</summary>
    /// <param name="session">The session.</param>
    /// <param name="output">The output.</param>
    /// <param name="instance">The display's instance id.</param>
    /// <param name="placement">Where they sit.</param>
    /// <returns>The controls, possibly none.</returns>
    public static IEnumerable<IntelControl> Build(
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
        caps.Size = (uint)sizeof(CtlSharpnessCaps);
        if (session.Observe(api.GetSharpnessCaps(output.Handle, &caps)) != IgclResult.Success
            || (caps.SupportedFilterFlags & (NonAdaptive | Adaptive)) == 0)
        {
            return [];
        }

        var count = Math.Clamp((int)caps.NumFilterTypes, 0, 8);
        var filters = new CtlSharpnessFilterProperties[Math.Max(count, 1)];
        var result = IgclResult.Success;
        if (count > 0)
        {
            fixed (CtlSharpnessFilterProperties* buffer = filters)
            {
                caps.FilterProperties = (nint)buffer;
                result = session.Observe(api.GetSharpnessCaps(output.Handle, &caps));
            }
        }

        List<IntelControl> controls =
        [
            new SharpnessControl(session, output, SharpnessField.Enable, caps.SupportedFilterFlags, null,
                Descriptors.Toggle("display.sharpening", instance, "Display sharpening", placement))
        ];

        List<(string, string)> choices = [];
        if ((caps.SupportedFilterFlags & NonAdaptive) != 0)
        {
            choices.Add(("non-adaptive", "Non-adaptive"));
        }

        if ((caps.SupportedFilterFlags & Adaptive) != 0)
        {
            choices.Add(("adaptive", "Adaptive"));
        }

        if (choices.Count > 1)
        {
            controls.Add(new SharpnessControl(session, output, SharpnessField.Filter, caps.SupportedFilterFlags, null,
                Descriptors.Choice("display.sharpening-filter", instance, "Sharpening filter", choices,
                    placement with { Order = placement.Order + 1 })));
        }

        if (result == IgclResult.Success && count > 0)
        {
            // One range for every filter: the union of what each reports, clamped per write.
            var used = filters.Take(count).Where(filter => filter.Maximum > filter.Minimum).ToArray();
            if (used.Length > 0
                && ValueMapping.FromFloat(used.Min(filter => filter.Minimum), used.Max(filter => filter.Maximum),
                    used.Min(filter => filter.Step), false) is { } range)
            {
                controls.Add(new SharpnessControl(session, output, SharpnessField.Intensity,
                    caps.SupportedFilterFlags, range,
                    Descriptors.Range("display.sharpening-intensity", instance, "Sharpening intensity",
                        range.Minimum, range.Maximum, range.Step, CapabilityUnit.None,
                        placement with { Order = placement.Order + 2 })));
            }
        }

        return controls;
    }

    /// <inheritdoc />
    public override ControlRead Read()
    {
        var result = ReadSettings(out var settings);
        if (result != IgclResult.Success)
        {
            return ControlRead.Failed(result);
        }

        return _field switch
        {
            SharpnessField.Enable => ControlRead.Of(CapabilityValue.Boolean(settings.Enable != 0)),
            SharpnessField.Filter => ControlRead.Of(CapabilityValue.Choice(
                (settings.FilterType & Adaptive) != 0 ? "adaptive" : "non-adaptive")),
            _ => ControlRead.Of(CapabilityValue.Integer(_range!.Value.ToInteger(settings.Intensity)))
        };
    }

    /// <inheritdoc />
    public override ControlWrite Write(CapabilityValue value)
    {
        var read = ReadSettings(out var current);
        var request = current;
        request.Size = (uint)sizeof(CtlSharpnessSettings);
        if (read != IgclResult.Success || (request.FilterType & _supportedFilters) == 0)
        {
            request.FilterType = (_supportedFilters & Adaptive) != 0 ? Adaptive : NonAdaptive;
        }

        switch (_field)
        {
            case SharpnessField.Enable:
                request.Enable = value.BooleanValue == true ? (byte)1 : (byte)0;
                break;
            case SharpnessField.Filter:
                request.FilterType = value.ChoiceValue == "adaptive" ? Adaptive : NonAdaptive;
                break;
            default:
                request.Intensity = (float)_range!.Value.ToNative(value.IntegerValue ?? 0);
                break;
        }

        return ControlWrite.From(_session.Observe(_session.Api.SetSharpness(_output.Handle, &request)),
            "display sharpening");
    }

    private int ReadSettings(out CtlSharpnessSettings settings)
    {
        CtlSharpnessSettings current = default;
        current.Size = (uint)sizeof(CtlSharpnessSettings);
        var result = _session.Observe(_session.Api.GetSharpness(_output.Handle, &current));
        settings = current;
        return result;
    }
}

/// <summary>The wire format: colour model and bits per colour on the link.</summary>
internal sealed unsafe class WireFormatControl : IntelControl
{
    private const int OperationGet = 0;
    private const int OperationSet = 1;

    private static readonly (int Model, string Id, string Label)[] Models =
    [
        (0, "rgb", "RGB"),
        (1, "ycbcr420", "YCbCr 4:2:0"),
        (2, "ycbcr422", "YCbCr 4:2:2"),
        (3, "ycbcr444", "YCbCr 4:4:4")
    ];

    private static readonly (uint Flag, int Bits)[] Depths = [(1 << 0, 6), (1 << 1, 8), (1 << 2, 10), (1 << 3, 12)];

    private readonly IgclOutput _output;
    private readonly IgclSession _session;

    private WireFormatControl(IgclSession session, IgclOutput output, CapabilityDescriptor descriptor)
        : base(descriptor)
    {
        _session = session;
        _output = output;
    }

    /// <summary>Builds the control when the driver reports more than one format.</summary>
    /// <param name="session">The session.</param>
    /// <param name="output">The output.</param>
    /// <param name="instance">The display's instance id.</param>
    /// <param name="placement">Where it sits.</param>
    /// <returns>The control, or null.</returns>
    public static WireFormatControl? TryCreate(
        IgclSession session,
        IgclOutput output,
        string instance,
        Placement placement)
    {
        if (session.Api.GetSetWireFormat is null || Get(session, output, out var config) != IgclResult.Success)
        {
            return null;
        }

        List<(string, string)> choices = [];
        for (var index = 0; index < 4; index++)
        {
            var entry = config.SupportedAt(index);
            var model = Array.Find(Models, candidate => candidate.Model == entry.ColorModel);
            if (model.Id is null || entry.ColorDepth == 0)
            {
                continue;
            }

            foreach (var depth in Depths)
            {
                var id = $"{model.Id}-{depth.Bits}";
                if ((entry.ColorDepth & depth.Flag) != 0 && choices.All(choice => choice.Item1 != id))
                {
                    choices.Add((id, $"{model.Label}, {depth.Bits}-bit"));
                }
            }
        }

        return choices.Count < 2
            ? null
            : new WireFormatControl(session, output,
                Descriptors.Choice("display.wire-format", instance, "Colour format", choices, placement));
    }

    /// <inheritdoc />
    public override ControlRead Read()
    {
        var result = Get(_session, _output, out var config);
        if (result != IgclResult.Success)
        {
            return ControlRead.Failed(result);
        }

        var model = Array.Find(Models, candidate => candidate.Model == config.Current.ColorModel);
        var depth = Array.Find(Depths, candidate => (config.Current.ColorDepth & candidate.Flag) != 0);
        var id = $"{model.Id}-{depth.Bits}";
        return model.Id is not null && depth.Bits != 0 && Descriptor.Choices.Any(choice => choice.Value == id)
            ? ControlRead.Of(CapabilityValue.Choice(id))
            : ControlRead.Failed(result);
    }

    /// <inheritdoc />
    public override ControlWrite Write(CapabilityValue value)
    {
        var parts = (value.ChoiceValue ?? "").Split('-');
        var model = Array.Find(Models, candidate => candidate.Id == parts[0]);
        var bits = parts.Length > 1 && int.TryParse(parts[1], out var parsed) ? parsed : 0;
        var depth = Array.Find(Depths, candidate => candidate.Bits == bits);
        if (model.Id is null || depth.Flag == 0)
        {
            return ControlWrite.Refuse($"'{value.ChoiceValue}' is not a wire format.");
        }

        CtlWireFormatConfig request = default;
        request.Size = (uint)sizeof(CtlWireFormatConfig);
        request.Operation = OperationSet;
        request.Current.Size = (uint)sizeof(CtlWireFormat);
        request.Current.ColorModel = model.Model;
        request.Current.ColorDepth = depth.Flag;
        return ControlWrite.From(_session.Observe(_session.Api.GetSetWireFormat(_output.Handle, &request)),
            "the wire format");
    }

    private static int Get(IgclSession session, IgclOutput output, out CtlWireFormatConfig config)
    {
        CtlWireFormatConfig request = default;
        request.Size = (uint)sizeof(CtlWireFormatConfig);
        request.Operation = OperationGet;
        var result = session.Observe(session.Api.GetSetWireFormat(output.Handle, &request));
        config = request;
        return result;
    }
}

/// <summary>Which end-display setting a control publishes.</summary>
internal enum DisplaySettingField
{
    LowLatency,
    SourceToneMapping,
    ContentType,
    QuantizationRange
}

/// <summary>
///     The end-display settings signalled through info frames: quantization range, content type,
///     low latency mode and source tone mapping. Only those the display reports as both supported and
///     controllable are offered.
/// </summary>
internal sealed unsafe class DisplaySettingControl : IntelControl
{
    private readonly DisplaySettingField _field;
    private readonly IReadOnlyList<(int Value, string Id, string Label)> _values;
    private readonly IgclOutput _output;
    private readonly IgclSession _session;

    private DisplaySettingControl(
        IgclSession session,
        IgclOutput output,
        DisplaySettingField field,
        IReadOnlyList<(int Value, string Id, string Label)> values,
        CapabilityDescriptor descriptor)
        : base(descriptor)
    {
        _session = session;
        _output = output;
        _field = field;
        _values = values;
    }

    /// <summary>Builds the settings the display offers.</summary>
    /// <param name="session">The session.</param>
    /// <param name="output">The output.</param>
    /// <param name="instance">The display's instance id.</param>
    /// <param name="placement">Where they sit.</param>
    /// <returns>The controls, possibly none.</returns>
    public static IEnumerable<IntelControl> Build(
        IgclSession session,
        IgclOutput output,
        string instance,
        Placement placement)
    {
        if (session.Api.GetSetDisplaySettings is null || Get(session, output, out var settings) != IgclResult.Success)
        {
            return [];
        }

        var usable = settings.SupportedFlags & settings.ControllableFlags;
        List<IntelControl> controls = [];
        Add(1 << 3, DisplaySettingField.QuantizationRange, "display.quantization-range", "Quantization range",
        [
            (0, "default", "Default"),
            (1, "limited", "Limited range"),
            (2, "full", "Full range")
        ]);
        Add(1 << 2, DisplaySettingField.ContentType, "display.content-type", "Content type",
        [
            (0, "default", "Default"),
            (1, "off", "Off"),
            (2, "desktop", "Desktop"),
            (3, "media", "Media"),
            (4, "gaming", "Gaming")
        ]);
        Add(1 << 0, DisplaySettingField.LowLatency, "display.low-latency", "Auto low latency mode",
        [
            (0, "default", "Default"),
            (1, "off", "Off"),
            (2, "on", "On")
        ]);
        Add(1 << 1, DisplaySettingField.SourceToneMapping, "display.source-tone-mapping", "Source tone mapping",
        [
            (0, "default", "Default"),
            (1, "off", "Off"),
            (2, "on", "On")
        ]);
        return controls;

        void Add(
            uint flag,
            DisplaySettingField field,
            string id,
            string label,
            (int Value, string Id, string Label)[] values)
        {
            if ((usable & flag) == 0)
            {
                return;
            }

            controls.Add(new DisplaySettingControl(session, output, field, values,
                Descriptors.Choice(id, instance, label, [.. values.Select(value => (value.Id, value.Label))],
                    placement with { Order = placement.Order + controls.Count })));
        }
    }

    /// <inheritdoc />
    public override ControlRead Read()
    {
        var result = Get(_session, _output, out var settings);
        if (result != IgclResult.Success)
        {
            return ControlRead.Failed(result);
        }

        var raw = _field switch
        {
            DisplaySettingField.LowLatency => settings.LowLatency,
            DisplaySettingField.SourceToneMapping => settings.SourceToneMapping,
            DisplaySettingField.ContentType => settings.ContentType,
            _ => settings.QuantizationRange
        };
        foreach (var value in _values)
        {
            if (value.Value == raw)
            {
                return ControlRead.Of(CapabilityValue.Choice(value.Id));
            }
        }

        return ControlRead.Failed(result);
    }

    /// <inheritdoc />
    public override ControlWrite Write(CapabilityValue value)
    {
        var raw = _values.FirstOrDefault(entry => entry.Id == value.ChoiceValue).Value;
        CtlDisplaySettings request = default;
        request.Size = (uint)sizeof(CtlDisplaySettings);
        request.Set = 1;
        switch (_field)
        {
            case DisplaySettingField.LowLatency:
                request.ValidFlags = 1 << 0;
                request.LowLatency = raw;
                break;
            case DisplaySettingField.SourceToneMapping:
                request.ValidFlags = 1 << 1;
                request.SourceToneMapping = raw;
                break;
            case DisplaySettingField.ContentType:
                request.ValidFlags = 1 << 2;
                request.ContentType = raw;
                break;
            default:
                request.ValidFlags = 1 << 3;
                request.QuantizationRange = raw;
                break;
        }

        return ControlWrite.From(_session.Observe(_session.Api.GetSetDisplaySettings(_output.Handle, &request)),
            Descriptor.Display.CustomLabel ?? CapabilityId);
    }

    private static int Get(IgclSession session, IgclOutput output, out CtlDisplaySettings settings)
    {
        CtlDisplaySettings request = default;
        request.Size = (uint)sizeof(CtlDisplaySettings);
        request.Set = 0;
        var result = session.Observe(session.Api.GetSetDisplaySettings(output.Handle, &request));
        settings = request;
        return result;
    }
}
