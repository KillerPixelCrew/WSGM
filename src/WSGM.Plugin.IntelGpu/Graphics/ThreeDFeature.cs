using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.IntelGpu.Controls;
using WSGM.Plugin.IntelGpu.Igcl;

namespace WSGM.Plugin.IntelGpu.Graphics;

/// <summary>The value of one 3D feature in whichever shape its value type uses.</summary>
internal struct RawFeatureValue
{
    public CtlPropertyValue Scalar;
    public CtlEnduranceGaming Endurance;
    public CtlAdaptiveSyncGetSet AdaptiveSync;
}

/// <summary>Which shape a feature's value travels in.</summary>
internal enum FeatureShape
{
    /// <summary>The property union: bool, enum, int, uint or float.</summary>
    Scalar,

    /// <summary><c>ctl_endurance_gaming_t</c> through the custom pointer.</summary>
    Endurance,

    /// <summary><c>ctl_adaptivesync_getset_t</c> through the custom pointer.</summary>
    AdaptiveSync
}

/// <summary>
///     One 3D feature of one adapter: the driver path for its global and per-application values, and
///     the controls that publish its fields.
/// </summary>
/// <remarks>
///     Every get and set uses the value type the driver reported in its feature table. That is not a
///     formality: reading a custom-typed feature with a scalar type crashed the driver process on
///     2026-09-29, so nothing here ever probes a type.
/// </remarks>
internal sealed unsafe class ThreeDFeature
{
    private readonly IgclSession _session;
    private RawFeatureValue _lastGlobal;
    private bool _lastGlobalKnown;

    public ThreeDFeature(
        IgclSession session,
        IgclAdapter adapter,
        Ctl3dFeatureDetails details,
        ThreeDFeatureInfo info,
        FeatureShape shape)
    {
        _session = session;
        Adapter = adapter;
        Details = details;
        Info = info;
        Shape = shape;
    }

    public IgclAdapter Adapter { get; }

    public Ctl3dFeatureDetails Details { get; }

    public ThreeDFeatureInfo Info { get; }

    public FeatureShape Shape { get; }

    /// <summary>Endurance Gaming's capability structure, when the driver answered for it.</summary>
    public CtlEnduranceGamingCaps? EnduranceCaps { get; set; }

    /// <summary>Adaptive Sync Plus's capability structure, when the driver answered for it.</summary>
    public CtlAdaptiveSyncCaps? AdaptiveSyncCaps { get; set; }

    /// <summary>Whether the driver keeps per-application values for it.</summary>
    public bool PerApplication => Details.PerAppSupport != 0;

    /// <summary>Whether a change reaches a running game.</summary>
    public bool LiveChange => (Details.FeatureMiscSupport & ThreeDFeatureCatalog.MiscLiveChange) != 0;

    /// <summary>What the driver means when it has no stored value: the defaults its table reports.</summary>
    public RawFeatureValue DefaultValue()
    {
        RawFeatureValue value = default;
        switch (Shape)
        {
            case FeatureShape.Endurance:
                value.Endurance.Control = EnduranceCaps?.ControlCaps.DefaultType ?? 0;
                value.Endurance.Mode = EnduranceCaps?.ModeCaps.DefaultType ?? 0;
                break;
            case FeatureShape.AdaptiveSync:
                value.AdaptiveSync.AdaptiveBalanceStrength = AdaptiveSyncCaps?.StrengthDefault ?? 0;
                break;
            case FeatureShape.Scalar:
            default:
                switch ((IgclValueType)Details.ValueType)
                {
                    case IgclValueType.Bool:
                        value.Scalar.Enable = Details.Value.DefaultEnable;
                        break;
                    case IgclValueType.Enum:
                        value.Scalar.EnumValue = Details.Value.EnumDefaultType;
                        break;
                    case IgclValueType.Int32:
                        value.Scalar.Enable = Details.Value.DefaultEnable;
                        value.Scalar.IntValue = Details.Value.IntDefault;
                        break;
                    case IgclValueType.UInt32:
                        value.Scalar.Enable = Details.Value.DefaultEnable;
                        value.Scalar.UIntValue = Details.Value.UIntDefault;
                        break;
                    case IgclValueType.Float:
                        value.Scalar.Enable = Details.Value.DefaultEnable;
                        value.Scalar.FloatValue = Details.Value.FloatDefault;
                        break;
                }

                break;
        }

        return value;
    }

    /// <summary>Reads the global value, or one application's.</summary>
    /// <param name="application">The executable name, or null for the global value.</param>
    /// <param name="value">The value, when this returns success.</param>
    /// <returns>
    ///     The driver result. <c>CTL_RESULT_ERROR_DATA_NOT_FOUND</c> means nothing is stored, which is
    ///     the driver default, so it is answered as success with the default value.
    /// </returns>
    public int Read(string? application, out RawFeatureValue value)
    {
        value = default;
        Ctl3dFeatureGetSet request = default;
        request.FeatureType = Details.FeatureType;
        request.ValueType = Details.ValueType;
        int result;
        switch (Shape)
        {
            case FeatureShape.Endurance:
            {
                CtlEnduranceGaming custom = default;
                request.CustomValueSize = sizeof(CtlEnduranceGaming);
                request.CustomValue = (nint)(&custom);
                result = _session.GetSet3dFeature(Adapter, ref request, application);
                value.Endurance = custom;
                break;
            }
            case FeatureShape.AdaptiveSync:
            {
                CtlAdaptiveSyncGetSet custom = default;
                request.CustomValueSize = sizeof(CtlAdaptiveSyncGetSet);
                request.CustomValue = (nint)(&custom);
                result = _session.GetSet3dFeature(Adapter, ref request, application);
                value.AdaptiveSync = custom;
                break;
            }
            case FeatureShape.Scalar:
            default:
                result = _session.GetSet3dFeature(Adapter, ref request, application);
                value.Scalar = request.Value;
                break;
        }

        if (result == IgclResult.DataNotFound)
        {
            value = DefaultValue();
            result = IgclResult.Success;
        }

        if (result == IgclResult.Success && application is null)
        {
            _lastGlobal = value;
            _lastGlobalKnown = true;
        }

        return result;
    }

    /// <summary>Writes the global value, or one application's.</summary>
    /// <param name="application">The executable name, or null for the global value.</param>
    /// <param name="value">The whole value.</param>
    /// <returns>The driver result.</returns>
    public int Write(string? application, RawFeatureValue value)
    {
        Ctl3dFeatureGetSet request = default;
        request.FeatureType = Details.FeatureType;
        request.ValueType = Details.ValueType;
        request.Set = 1;
        switch (Shape)
        {
            case FeatureShape.Endurance:
            {
                var custom = value.Endurance;
                request.CustomValueSize = sizeof(CtlEnduranceGaming);
                request.CustomValue = (nint)(&custom);
                return _session.GetSet3dFeature(Adapter, ref request, application);
            }
            case FeatureShape.AdaptiveSync:
            {
                var custom = value.AdaptiveSync;
                request.CustomValueSize = sizeof(CtlAdaptiveSyncGetSet);
                request.CustomValue = (nint)(&custom);
                return _session.GetSet3dFeature(Adapter, ref request, application);
            }
            case FeatureShape.Scalar:
            default:
                request.Value = value.Scalar;
                return _session.GetSet3dFeature(Adapter, ref request, application);
        }
    }

    /// <summary>
    ///     The global value a field write starts from: a fresh read, or the last good one, or the
    ///     driver default.
    /// </summary>
    /// <returns>The value the other fields keep.</returns>
    /// <remarks>
    ///     A feature whose value is a structure holds several controls, and writing one field has to
    ///     carry the others. The read here supplies those fields; it never decides whether to write.
    /// </remarks>
    public RawFeatureValue BaseForWrite()
    {
        if (Shape == FeatureShape.Scalar)
        {
            return DefaultValue();
        }

        if (Read(null, out var current) == IgclResult.Success)
        {
            return current;
        }

        return _lastGlobalKnown ? _lastGlobal : DefaultValue();
    }
}

/// <summary>Which field of a 3D feature one control publishes.</summary>
internal enum FeatureField
{
    Scalar,
    EnduranceControl,
    EnduranceMode,
    AdaptiveSync,
    AdaptiveBalance,
    AdaptiveBalanceStrength,
    AllowTearing
}

/// <summary>One published field of one 3D feature.</summary>
internal sealed class ThreeDFeatureControl : IntelControl
{
    private static readonly EnumMember[] EnduranceControls =
    [
        new(0, "off", "Off"),
        new(1, "on", "On"),
        new(2, "auto", "Auto")
    ];

    private static readonly EnumMember[] EnduranceModes =
    [
        new(0, "performance", "Better performance"),
        new(1, "balanced", "Balanced"),
        new(2, "battery", "Maximum battery")
    ];

    private readonly IReadOnlyList<EnumMember> _members;
    private readonly IntegerRange? _range;

    private ThreeDFeatureControl(
        CapabilityDescriptor descriptor,
        ThreeDFeature feature,
        FeatureField field,
        IReadOnlyList<EnumMember> members,
        IntegerRange? range)
        : base(descriptor)
    {
        Feature = feature;
        Field = field;
        _members = members;
        _range = range;
    }

    public ThreeDFeature Feature { get; }

    public FeatureField Field { get; }

    /// <summary>Builds the controls for one reported feature.</summary>
    /// <param name="feature">The feature.</param>
    /// <param name="instance">The adapter's instance id.</param>
    /// <param name="placement">Where the feature's controls sit.</param>
    /// <param name="log">Receives why a feature publishes nothing.</param>
    /// <returns>The controls, possibly none.</returns>
    public static IReadOnlyList<ThreeDFeatureControl> Build(
        ThreeDFeature feature,
        string instance,
        Placement placement,
        IntelLog log)
    {
        var id = ThreeDFeatureCatalog.CapabilityId(feature.Info);
        var label = feature.Info.Label;
        List<ThreeDFeatureControl> controls = [];
        switch (feature.Shape)
        {
            case FeatureShape.Endurance:
            {
                var controlMembers = Filter(EnduranceControls, feature.EnduranceCaps?.ControlCaps.SupportedTypes ?? 0);
                var modeMembers = Filter(EnduranceModes, feature.EnduranceCaps?.ModeCaps.SupportedTypes ?? 0);
                if (controlMembers.Count > 1)
                {
                    controls.Add(ChoiceControl(feature, FeatureField.EnduranceControl, id, instance, label,
                        controlMembers, placement));
                }

                if (modeMembers.Count > 1)
                {
                    controls.Add(ChoiceControl(feature, FeatureField.EnduranceMode, $"{id}-target", instance,
                        "Endurance Gaming target", modeMembers, placement with { Order = placement.Order + 1 }));
                }

                break;
            }
            case FeatureShape.AdaptiveSync:
            {
                controls.Add(ToggleControl(feature, FeatureField.AdaptiveSync, "graphics.adaptive-sync", instance,
                    "Adaptive sync", placement));
                controls.Add(ToggleControl(feature, FeatureField.AllowTearing, "graphics.adaptive-sync-tearing",
                    instance, "Tear above max refresh", placement with { Order = placement.Order + 3 }));
                if (feature.AdaptiveSyncCaps is { AdaptiveBalanceSupported: not 0 } caps)
                {
                    controls.Add(ToggleControl(feature, FeatureField.AdaptiveBalance, "graphics.adaptive-balance",
                        instance, "Adaptive balance", placement with { Order = placement.Order + 1 }));
                    if (ValueMapping.FromFloat(caps.StrengthMinimum, caps.StrengthMaximum, caps.StrengthStep, false)
                        is { } strength)
                    {
                        controls.Add(new ThreeDFeatureControl(
                            Descriptors.Range("graphics.adaptive-balance-strength", instance,
                                "Adaptive balance strength", strength.Minimum, strength.Maximum, strength.Step,
                                CapabilityUnit.None, Scoped(feature, placement with { Order = placement.Order + 2 })),
                            feature,
                            FeatureField.AdaptiveBalanceStrength,
                            [],
                            strength));
                    }
                }

                break;
            }
            case FeatureShape.Scalar:
            default:
            {
                var details = feature.Details;
                switch ((IgclValueType)details.ValueType)
                {
                    case IgclValueType.Bool:
                        controls.Add(ToggleControl(feature, FeatureField.Scalar, id, instance, label, placement));
                        break;
                    case IgclValueType.Enum:
                    {
                        var members = ThreeDFeatureCatalog.SupportedMembers(feature.Info,
                            details.Value.EnumSupportedTypes);
                        if (members.Count > 1)
                        {
                            controls.Add(ChoiceControl(feature, FeatureField.Scalar, id, instance, label, members,
                                placement));
                        }
                        else
                        {
                            log.Info("graphics",
                                $"{label} offers {members.Count} value(s) (mask 0x{details.Value.EnumSupportedTypes:x}); not published.");
                        }

                        break;
                    }
                    case IgclValueType.Int32:
                    case IgclValueType.UInt32:
                    case IgclValueType.Float:
                    {
                        var range = (IgclValueType)details.ValueType switch
                        {
                            IgclValueType.Int32 => ValueMapping.FromInt(details.Value.IntMinimum,
                                details.Value.IntMaximum, details.Value.IntStep, true),
                            IgclValueType.UInt32 => ValueMapping.FromUInt(details.Value.UIntMinimum,
                                details.Value.UIntMaximum, details.Value.UIntStep, true),
                            _ => ValueMapping.FromFloat(details.Value.FloatMinimum, details.Value.FloatMaximum,
                                details.Value.FloatStep, true)
                        };
                        if (range is { } usable)
                        {
                            controls.Add(new ThreeDFeatureControl(
                                Descriptors.Range(id, instance, label, usable.Minimum, usable.Maximum, usable.Step,
                                    CapabilityUnit.None, Scoped(feature, placement)),
                                feature,
                                FeatureField.Scalar,
                                [],
                                usable));
                        }
                        else
                        {
                            log.Info("graphics", $"{label} reports an unusable range; not published.");
                        }

                        break;
                    }
                }

                break;
            }
        }

        return controls;
    }

    /// <inheritdoc />
    public override ControlRead Read()
    {
        var result = Feature.Read(null, out var raw);
        return result == IgclResult.Success && Decode(raw) is { } value
            ? ControlRead.Of(value)
            : ControlRead.Failed(result);
    }

    /// <inheritdoc />
    public override ControlWrite Write(CapabilityValue value)
    {
        var raw = Encode(Feature.BaseForWrite(), value);
        return ControlWrite.From(Feature.Write(null, raw), $"{Descriptor.Display.CustomLabel ?? CapabilityId}");
    }

    /// <summary>Applies one of this control's values onto a whole feature value.</summary>
    /// <param name="current">The value the other fields keep.</param>
    /// <param name="value">The value for this control's field.</param>
    /// <returns>The combined value.</returns>
    public RawFeatureValue Encode(RawFeatureValue current, CapabilityValue value)
    {
        var raw = current;
        switch (Field)
        {
            case FeatureField.EnduranceControl:
                raw.Endurance.Control = MemberValue(value.ChoiceValue);
                break;
            case FeatureField.EnduranceMode:
                raw.Endurance.Mode = MemberValue(value.ChoiceValue);
                break;
            case FeatureField.AdaptiveSync:
                raw.AdaptiveSync.AdaptiveSync = Flag(value.BooleanValue);
                break;
            case FeatureField.AdaptiveBalance:
                raw.AdaptiveSync.AdaptiveBalance = Flag(value.BooleanValue);
                break;
            case FeatureField.AllowTearing:
                raw.AdaptiveSync.AllowAsyncForHighFps = Flag(value.BooleanValue);
                break;
            case FeatureField.AdaptiveBalanceStrength:
                raw.AdaptiveSync.AdaptiveBalanceStrength = (float)_range!.Value.ToNative(value.IntegerValue ?? 0);
                break;
            case FeatureField.Scalar:
            default:
                raw.Scalar = EncodeScalar(value);
                break;
        }

        return raw;
    }

    /// <summary>Reads this control's field out of a whole feature value.</summary>
    /// <param name="raw">The feature value.</param>
    /// <returns>The field in the descriptor's shape, or null when it has no offered equivalent.</returns>
    public CapabilityValue? Decode(RawFeatureValue raw)
    {
        switch (Field)
        {
            case FeatureField.EnduranceControl:
                return Member(raw.Endurance.Control);
            case FeatureField.EnduranceMode:
                return Member(raw.Endurance.Mode);
            case FeatureField.AdaptiveSync:
                return CapabilityValue.Boolean(raw.AdaptiveSync.AdaptiveSync != 0);
            case FeatureField.AdaptiveBalance:
                return CapabilityValue.Boolean(raw.AdaptiveSync.AdaptiveBalance != 0);
            case FeatureField.AllowTearing:
                return CapabilityValue.Boolean(raw.AdaptiveSync.AllowAsyncForHighFps != 0);
            case FeatureField.AdaptiveBalanceStrength:
                return CapabilityValue.Integer(_range!.Value.ToInteger(raw.AdaptiveSync.AdaptiveBalanceStrength));
            case FeatureField.Scalar:
            default:
                return DecodeScalar(raw.Scalar);
        }
    }

    /// <inheritdoc />
    public override bool Validate(CapabilityValue? value, out string? error)
    {
        if (!base.Validate(value, out error))
        {
            return false;
        }

        if (_range is { } range && value!.IntegerValue is { } integer && !range.Accepts(integer))
        {
            error = $"{integer} is not on the driver's range.";
            return false;
        }

        return true;
    }

    private CtlPropertyValue EncodeScalar(CapabilityValue value)
    {
        CtlPropertyValue scalar = default;
        switch ((IgclValueType)Feature.Details.ValueType)
        {
            case IgclValueType.Bool:
                scalar.Enable = Flag(value.BooleanValue);
                break;
            case IgclValueType.Enum:
                scalar.EnumValue = MemberValue(value.ChoiceValue);
                break;
            case IgclValueType.Int32:
            case IgclValueType.UInt32:
            case IgclValueType.Float:
            {
                var range = _range!.Value;
                var integer = value.IntegerValue ?? 0;
                var off = range.OffAtZero && integer == 0;
                var native = range.ToNative(off ? range.NativeMinimum : integer);
                scalar.Enable = off ? (byte)0 : (byte)1;
                switch ((IgclValueType)Feature.Details.ValueType)
                {
                    case IgclValueType.Int32:
                        scalar.IntValue = (int)native;
                        break;
                    case IgclValueType.UInt32:
                        scalar.UIntValue = (uint)Math.Max(0, native);
                        break;
                    default:
                        scalar.FloatValue = (float)native;
                        break;
                }

                break;
            }
        }

        return scalar;
    }

    private CapabilityValue? DecodeScalar(CtlPropertyValue scalar)
    {
        switch ((IgclValueType)Feature.Details.ValueType)
        {
            case IgclValueType.Bool:
                return CapabilityValue.Boolean(scalar.Enable != 0);
            case IgclValueType.Enum:
                return Member(scalar.EnumValue);
            case IgclValueType.Int32:
            case IgclValueType.UInt32:
            case IgclValueType.Float:
            {
                var range = _range!.Value;
                if (range.OffAtZero && scalar.Enable == 0)
                {
                    return CapabilityValue.Integer(0);
                }

                double native = (IgclValueType)Feature.Details.ValueType switch
                {
                    IgclValueType.Int32 => scalar.IntValue,
                    IgclValueType.UInt32 => scalar.UIntValue,
                    _ => scalar.FloatValue
                };
                return CapabilityValue.Integer(range.ToInteger(native));
            }
            default:
                return null;
        }
    }

    private CapabilityValue? Member(uint value)
    {
        foreach (var member in _members)
        {
            if (member.Value == value)
            {
                return CapabilityValue.Choice(member.Id);
            }
        }

        return null;
    }

    private uint MemberValue(string? id)
    {
        foreach (var member in _members)
        {
            if (member.Id == id)
            {
                return member.Value;
            }
        }

        return 0;
    }

    private static byte Flag(bool? value)
    {
        return value == true ? (byte)1 : (byte)0;
    }

    private static IReadOnlyList<EnumMember> Filter(IReadOnlyList<EnumMember> members, ulong mask)
    {
        return mask == 0
            ? members
            : [.. members.Where(member => (mask & ThreeDFeatureCatalog.MaskBit(EnumMaskKind.Ordinal, member.Value)) != 0)];
    }

    private static Placement Scoped(ThreeDFeature feature, Placement placement)
    {
        return placement with
        {
            Scope = feature.PerApplication
                ? CapabilityProfileScope.NativePerApplication
                : CapabilityProfileScope.Switched,
            Timing = feature.LiveChange
                ? CapabilityApplyTiming.Immediate
                : CapabilityApplyTiming.NextApplicationStart
        };
    }

    private static ThreeDFeatureControl ToggleControl(
        ThreeDFeature feature,
        FeatureField field,
        string id,
        string instance,
        string label,
        Placement placement)
    {
        return new ThreeDFeatureControl(
            Descriptors.Toggle(id, instance, label, Scoped(feature, placement)),
            feature,
            field,
            [],
            null);
    }

    private static ThreeDFeatureControl ChoiceControl(
        ThreeDFeature feature,
        FeatureField field,
        string id,
        string instance,
        string label,
        IReadOnlyList<EnumMember> members,
        Placement placement)
    {
        return new ThreeDFeatureControl(
            Descriptors.Choice(id, instance, label, [.. members.Select(member => (member.Id, member.Label))],
                Scoped(feature, placement)),
            feature,
            field,
            members,
            null);
    }
}
