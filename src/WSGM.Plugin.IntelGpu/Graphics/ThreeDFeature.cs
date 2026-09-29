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
    public Ctl3dAppProfiles AppProfile;
    public Ctl3dLiveState LiveState;
}

/// <summary>Which shape a feature's value travels in.</summary>
internal enum FeatureShape
{
    /// <summary>The property union: bool, enum, int, uint or float.</summary>
    Scalar,

    /// <summary><c>ctl_endurance_gaming_t</c> through the custom pointer.</summary>
    Endurance,

    /// <summary><c>ctl_adaptivesync_getset_t</c> through the custom pointer.</summary>
    AdaptiveSync,

    /// <summary><c>ctl_3d_app_profiles_t</c> through the custom pointer, for one tier type.</summary>
    AppProfile,

    /// <summary><c>ctl_3d_live_state_t</c> through the custom pointer; read only.</summary>
    LiveState
}

/// <summary>
///     One 3D feature of one adapter: the driver path for its global and per-application values, and
///     the controls that publish its fields.
/// </summary>
/// <remarks>
///     Every get and set uses the value type the driver reported in its feature table. That is not a
///     formality: reading a custom-typed feature with a scalar type crashed the driver process on
///     2026-09-29, so nothing here ever probes a type. The global value is read once per pass and shared
///     by every control of the feature.
/// </remarks>
internal sealed unsafe class ThreeDFeature
{
    private readonly IgclSession _session;
    private PassCache<RawFeatureValue> _global;
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

    /// <summary>
    ///     The <c>ctl_3d_tier_type_flag_t</c> a game profile feature reads and writes. Each tier type is its
    ///     own value in the driver, asked for through the structure's <c>TierType</c> input.
    /// </summary>
    public uint TierType { get; init; }

    /// <summary><c>SupportedTierProfiles</c> as the driver reported it for <see cref="TierType" />.</summary>
    public uint SupportedTierProfiles { get; set; }

    /// <summary><c>DefaultEnabledTierProfiles</c>: what the driver enables when nothing is stored.</summary>
    public uint DefaultEnabledTierProfiles { get; set; }

    /// <summary>Whether the driver keeps per-application values for it.</summary>
    /// <remarks>The live state is a status, never a per-application setting.</remarks>
    public bool PerApplication => Details.PerAppSupport != 0 && Shape != FeatureShape.LiveState;

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
            case FeatureShape.AppProfile:
                value.AppProfile.TierType = TierType;
                value.AppProfile.SupportedTierProfiles = SupportedTierProfiles;
                value.AppProfile.DefaultEnabledTierProfiles = DefaultEnabledTierProfiles;
                value.AppProfile.EnabledTierProfiles = DefaultEnabledTierProfiles;
                break;
            case FeatureShape.LiveState:
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

    /// <summary>Reads the global value, once per pass, or one application's.</summary>
    /// <param name="application">The executable name, or null for the global value.</param>
    /// <param name="value">The value, when this returns success.</param>
    /// <returns>
    ///     The driver result. <c>CTL_RESULT_ERROR_DATA_NOT_FOUND</c> means nothing is stored, which is
    ///     the driver default, so it is answered as success with the default value.
    /// </returns>
    public int Read(string? application, out RawFeatureValue value)
    {
        if (application is null && _global.TryGet(_session.Pass, out var cached, out value))
        {
            return cached;
        }

        value = default;
        int result;
        switch (Shape)
        {
            case FeatureShape.Endurance:
                result = GetSetCustom(ref value.Endurance, application, false);
                break;
            case FeatureShape.AdaptiveSync:
                result = GetSetCustom(ref value.AdaptiveSync, application, false);
                break;
            case FeatureShape.AppProfile:
                result = ReadAppProfile(application, out value.AppProfile);
                break;
            case FeatureShape.LiveState:
                result = GetSetCustom(ref value.LiveState, application, false);
                break;
            case FeatureShape.Scalar:
            default:
            {
                var request = Request(false);
                result = _session.GetSet3dFeature(Adapter, ref request, application);
                value.Scalar = request.Value;
                break;
            }
        }

        if (result == IgclResult.DataNotFound)
        {
            value = DefaultValue();
            result = IgclResult.Success;
        }

        if (application is null)
        {
            _global.Store(_session.Pass, result, value);
            if (result == IgclResult.Success)
            {
                _lastGlobal = value;
                _lastGlobalKnown = true;
            }
        }

        return result;
    }

    /// <summary>Writes the global value, or one application's.</summary>
    /// <param name="application">The executable name, or null for the global value.</param>
    /// <param name="value">The whole value.</param>
    /// <returns>The driver result.</returns>
    public int Write(string? application, RawFeatureValue value)
    {
        if (application is null)
        {
            _global.Invalidate();
        }

        switch (Shape)
        {
            case FeatureShape.Endurance:
                return GetSetCustom(ref value.Endurance, application, true);
            case FeatureShape.AdaptiveSync:
                return GetSetCustom(ref value.AdaptiveSync, application, true);
            case FeatureShape.AppProfile:
                // The tier type is the call's input, so it is always this feature's, whatever the value
                // the other fields came from.
                value.AppProfile.TierType = TierType;
                return GetSetCustom(ref value.AppProfile, application, true);
            case FeatureShape.LiveState:
                return IgclResult.NotImplemented;
            case FeatureShape.Scalar:
            default:
            {
                var request = Request(true);
                request.Value = value.Scalar;
                return _session.GetSet3dFeature(Adapter, ref request, application);
            }
        }
    }

    /// <summary>
    ///     Asks the driver for one tier type's game profile, keeping what it reports even when nothing is
    ///     stored.
    /// </summary>
    /// <param name="application">The executable name, or null for the global value.</param>
    /// <param name="profile">What the driver filled in.</param>
    /// <returns>The driver result, <c>CTL_RESULT_ERROR_DATA_NOT_FOUND</c> included.</returns>
    /// <remarks>
    ///     Intel's sample reads <c>DefaultEnabledTierProfiles</c> after a get that answered
    ///     <c>CTL_RESULT_ERROR_DATA_NOT_FOUND</c>, so the output fields are kept either way. The request
    ///     always carries the custom value type and a buffer of the header's 32 bytes.
    /// </remarks>
    public int ReadAppProfile(string? application, out Ctl3dAppProfiles profile)
    {
        profile = default;
        profile.TierType = TierType;
        return GetSetCustom(ref profile, application, false);
    }

    /// <summary>
    ///     The global value a field write starts from: this pass's read, or the last good one, or the
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

    /// <summary>A get or set of this feature, carrying the value type the driver reported.</summary>
    private Ctl3dFeatureGetSet Request(bool set)
    {
        Ctl3dFeatureGetSet request = default;
        request.FeatureType = Details.FeatureType;
        request.ValueType = Details.ValueType;
        request.Set = set ? (byte)1 : (byte)0;
        return request;
    }

    /// <summary>Gets or sets a custom-typed value through the custom pointer.</summary>
    /// <typeparam name="T">The feature's structure.</typeparam>
    /// <param name="custom">The structure; filled on a get.</param>
    /// <param name="application">The executable name, or null for the global value.</param>
    /// <param name="set">Whether this is a set.</param>
    /// <returns>The driver result.</returns>
    private int GetSetCustom<T>(ref T custom, string? application, bool set)
        where T : unmanaged
    {
        var request = Request(set);
        request.CustomValueSize = sizeof(T);
        fixed (T* pointer = &custom)
        {
            request.CustomValue = (nint)pointer;
            return _session.GetSet3dFeature(Adapter, ref request, application);
        }
    }
}

/// <summary>One published field of one 3D feature.</summary>
/// <remarks>
///     A field is two functions over the feature's whole value: how to read the field and how to set it.
///     Controls of one structure-valued feature share that value, so writing one field carries the
///     others.
/// </remarks>
internal sealed class ThreeDFeatureControl : IntelControl
{
    /// <summary>The largest target frame rate the live state row shows; anything above reads as it.</summary>
    private const int MaxLiveFps = 1000;

    private readonly Decoder _decode;
    private readonly Encoder? _encode;

    private ThreeDFeatureControl(
        CapabilityDescriptor descriptor,
        ThreeDFeature feature,
        Decoder decode,
        Encoder? encode,
        IReadOnlyList<EnumMember>? members = null,
        IntegerRange? range = null)
        : base(descriptor, members, range)
    {
        Feature = feature;
        _decode = decode;
        _encode = encode;
    }

    private delegate CapabilityValue? Decoder(ThreeDFeatureControl control, RawFeatureValue raw);

    private delegate void Encoder(ThreeDFeatureControl control, ref RawFeatureValue raw, CapabilityValue value);

    public ThreeDFeature Feature { get; }

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
                BuildEndurance(feature, id, instance, label, placement, controls);
                break;
            case FeatureShape.AdaptiveSync:
                BuildAdaptiveSync(feature, instance, placement, controls);
                break;
            case FeatureShape.AppProfile:
            {
                var type = EnumMembers.ByValue(ThreeDFeatureCatalog.TierTypeMembers, feature.TierType);
                var members = ThreeDFeatureCatalog.SupportedTiers(feature.SupportedTierProfiles);
                if (type is not null && members.Count > 1)
                {
                    controls.Add(Choice(feature, type.Id, instance, type.Label, members, placement,
                        static (control, raw) => control.MemberOf(raw.AppProfile.EnabledTierProfiles),
                        static (control, ref raw, value) =>
                        {
                            raw.AppProfile.TierType = control.Feature.TierType;
                            raw.AppProfile.EnabledTierProfiles = control.ValueOf(value);
                        }));
                }
                else
                {
                    log.Info("graphics",
                        $"{label} tier type 0x{feature.TierType:x} offers {members.Count} value(s) "
                        + $"(mask 0x{feature.SupportedTierProfiles:x}); not published.");
                }

                break;
            }
            case FeatureShape.LiveState:
                BuildLiveState(feature, instance, placement, controls);
                break;
            case FeatureShape.Scalar:
            default:
                BuildScalar(feature, id, instance, label, placement, controls, log);
                break;
        }

        return controls;
    }

    /// <inheritdoc />
    public override ControlRead Read()
    {
        var result = Feature.Read(null, out var raw);
        return result == IgclResult.Success ? ControlRead.Of(Decode(raw)) : ControlRead.Driver(result);
    }

    /// <summary>Applies one of this control's values onto a whole feature value.</summary>
    /// <param name="current">The value the other fields keep.</param>
    /// <param name="value">The value for this control's field.</param>
    /// <returns>The combined value.</returns>
    public RawFeatureValue Encode(RawFeatureValue current, CapabilityValue value)
    {
        var raw = current;
        _encode?.Invoke(this, ref raw, value);
        return raw;
    }

    /// <summary>Reads this control's field out of a whole feature value.</summary>
    /// <param name="raw">The feature value.</param>
    /// <returns>The field in the descriptor's shape, or null when it has no offered equivalent.</returns>
    public CapabilityValue? Decode(RawFeatureValue raw)
    {
        return _decode(this, raw);
    }

    /// <inheritdoc />
    protected override ControlWrite WriteValidated(CapabilityValue value)
    {
        var raw = Encode(Feature.BaseForWrite(), value);
        return ControlWrite.From(Feature.Write(null, raw), Descriptor.Display.CustomLabel ?? CapabilityId);
    }

    private static void BuildEndurance(
        ThreeDFeature feature,
        string id,
        string instance,
        string label,
        Placement placement,
        List<ThreeDFeatureControl> controls)
    {
        var controlMembers = EnumMembers.Offered(ThreeDFeatureCatalog.EnduranceControls,
            feature.EnduranceCaps?.ControlCaps.SupportedTypes ?? 0, EnumMaskKind.Ordinal);
        var modeMembers = EnumMembers.Offered(ThreeDFeatureCatalog.EnduranceModes,
            feature.EnduranceCaps?.ModeCaps.SupportedTypes ?? 0, EnumMaskKind.Ordinal);
        if (controlMembers.Count > 1)
        {
            controls.Add(Choice(feature, id, instance, label, controlMembers, placement,
                static (control, raw) => control.MemberOf(raw.Endurance.Control),
                static (control, ref raw, value) => raw.Endurance.Control = control.ValueOf(value)));
        }

        if (modeMembers.Count > 1)
        {
            controls.Add(Choice(feature, $"{id}-target", instance, "Endurance Gaming target", modeMembers,
                placement.Plus(1),
                static (control, raw) => control.MemberOf(raw.Endurance.Mode),
                static (control, ref raw, value) => raw.Endurance.Mode = control.ValueOf(value)));
        }
    }

    private static void BuildAdaptiveSync(
        ThreeDFeature feature,
        string instance,
        Placement placement,
        List<ThreeDFeatureControl> controls)
    {
        controls.Add(Toggle(feature, "graphics.adaptive-sync", instance, "Adaptive sync", placement,
            static (_, raw) => Boolean(raw.AdaptiveSync.AdaptiveSync != 0),
            static (_, ref raw, value) => raw.AdaptiveSync.AdaptiveSync = Flag(value)));
        controls.Add(Toggle(feature, "graphics.adaptive-sync-tearing", instance, "Tear above max refresh",
            placement.Plus(3),
            static (_, raw) => Boolean(raw.AdaptiveSync.AllowAsyncForHighFps != 0),
            static (_, ref raw, value) => raw.AdaptiveSync.AllowAsyncForHighFps = Flag(value)));
        if (feature.AdaptiveSyncCaps is not { AdaptiveBalanceSupported: not 0 } caps)
        {
            return;
        }

        controls.Add(Toggle(feature, "graphics.adaptive-balance", instance, "Adaptive balance", placement.Plus(1),
            static (_, raw) => Boolean(raw.AdaptiveSync.AdaptiveBalance != 0),
            static (_, ref raw, value) => raw.AdaptiveSync.AdaptiveBalance = Flag(value)));
        if (ValueMapping.FromFloat(caps.StrengthMinimum, caps.StrengthMaximum, caps.StrengthStep, false)
            is not { } strength)
        {
            return;
        }

        controls.Add(new ThreeDFeatureControl(
            Descriptors.Range("graphics.adaptive-balance-strength", instance, "Adaptive balance strength", strength,
                CapabilityUnit.None, Scoped(feature, placement.Plus(2))),
            feature,
            static (control, raw) =>
                CapabilityValue.Integer(control.Range!.Value.ToInteger(raw.AdaptiveSync.AdaptiveBalanceStrength)),
            static (control, ref raw, value) => raw.AdaptiveSync.AdaptiveBalanceStrength =
                (float)control.Range!.Value.ToNative(value.IntegerValue!.Value),
            range: strength));
    }

    private static void BuildLiveState(
        ThreeDFeature feature,
        string instance,
        Placement placement,
        List<ThreeDFeatureControl> controls)
    {
        controls.Add(new ThreeDFeatureControl(
            Descriptors.ReadOnlyChoice("graphics.live-api", instance, "Active graphics API",
                ThreeDFeatureCatalog.LiveApis, placement),
            feature,
            static (control, raw) => control.MemberOf(ThreeDFeatureCatalog.LiveApi(raw.LiveState.GraphicsApi)),
            null,
            ThreeDFeatureCatalog.LiveApis));
        controls.Add(new ThreeDFeatureControl(
            Descriptors.ReadOnlyRange("graphics.live-target-fps", instance, "Frame pacing target (FPS)",
                IntegerRange.Linear(0, MaxLiveFps), CapabilityUnit.None, placement.Plus(1)),
            feature,
            static (_, raw) => CapabilityValue.Integer((int)Math.Min(raw.LiveState.TargetFps, MaxLiveFps)),
            null));
        controls.Add(new ThreeDFeatureControl(
            Descriptors.ReadOnlyChoice("graphics.live-frame-pacing", instance, "Frame pacing status",
                ThreeDFeatureCatalog.LiveFramePacing, placement.Plus(2)),
            feature,
            static (control, raw) => control.MemberOf(raw.LiveState.FramePacingStatus),
            null,
            ThreeDFeatureCatalog.LiveFramePacing));
    }

    private static void BuildScalar(
        ThreeDFeature feature,
        string id,
        string instance,
        string label,
        Placement placement,
        List<ThreeDFeatureControl> controls,
        IntelLog log)
    {
        var details = feature.Details;
        switch ((IgclValueType)details.ValueType)
        {
            case IgclValueType.Bool:
                controls.Add(Toggle(feature, id, instance, label, placement,
                    static (_, raw) => Boolean(raw.Scalar.Enable != 0),
                    static (_, ref raw, value) =>
                    {
                        raw.Scalar = default;
                        raw.Scalar.Enable = Flag(value);
                    }));
                break;
            case IgclValueType.Enum:
            {
                var members = ThreeDFeatureCatalog.SupportedMembers(feature.Info, details.Value.EnumSupportedTypes);
                if (members.Count > 1)
                {
                    controls.Add(Choice(feature, id, instance, label, members, placement,
                        static (control, raw) => control.MemberOf(raw.Scalar.EnumValue),
                        static (control, ref raw, value) =>
                        {
                            raw.Scalar = default;
                            raw.Scalar.EnumValue = control.ValueOf(value);
                        }));
                }
                else
                {
                    log.Info("graphics",
                        $"{label} offers {members.Count} value(s) (mask 0x{details.Value.EnumSupportedTypes:x}); "
                        + "not published.");
                }

                break;
            }
            case IgclValueType.Int32:
            case IgclValueType.UInt32:
            case IgclValueType.Float:
            {
                var range = (IgclValueType)details.ValueType switch
                {
                    IgclValueType.Int32 => ValueMapping.FromInt(details.Value.IntMinimum, details.Value.IntMaximum,
                        details.Value.IntStep, true),
                    IgclValueType.UInt32 => ValueMapping.FromUInt(details.Value.UIntMinimum,
                        details.Value.UIntMaximum, details.Value.UIntStep, true),
                    _ => ValueMapping.FromFloat(details.Value.FloatMinimum, details.Value.FloatMaximum,
                        details.Value.FloatStep, true)
                };
                if (range is not { } usable)
                {
                    log.Info("graphics", $"{label} reports an unusable range; not published.");
                    break;
                }

                controls.Add(new ThreeDFeatureControl(
                    Descriptors.Range(id, instance, label, usable, CapabilityUnit.None, Scoped(feature, placement)),
                    feature,
                    static (control, raw) => control.DecodeNumber(raw.Scalar),
                    static (control, ref raw, value) => raw.Scalar = control.EncodeNumber(value),
                    range: usable));
                break;
            }
        }
    }

    private CtlPropertyValue EncodeNumber(CapabilityValue value)
    {
        var range = Range!.Value;
        var integer = value.IntegerValue!.Value;
        var off = range.OffAtZero && integer == 0;
        var native = range.ToNative(off ? range.NativeMinimum : integer);
        CtlPropertyValue scalar = default;
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

        return scalar;
    }

    private CapabilityValue DecodeNumber(CtlPropertyValue scalar)
    {
        var range = Range!.Value;
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

    private static byte Flag(CapabilityValue value)
    {
        return value.BooleanValue == true ? (byte)1 : (byte)0;
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

    private static ThreeDFeatureControl Toggle(
        ThreeDFeature feature,
        string id,
        string instance,
        string label,
        Placement placement,
        Decoder decode,
        Encoder encode)
    {
        return new ThreeDFeatureControl(
            Descriptors.Toggle(id, instance, label, Scoped(feature, placement)),
            feature,
            decode,
            encode);
    }

    private static ThreeDFeatureControl Choice(
        ThreeDFeature feature,
        string id,
        string instance,
        string label,
        IReadOnlyList<EnumMember> members,
        Placement placement,
        Decoder decode,
        Encoder encode)
    {
        return new ThreeDFeatureControl(
            Descriptors.Choice(id, instance, label, members, Scoped(feature, placement)),
            feature,
            decode,
            encode,
            members);
    }
}
