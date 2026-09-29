using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.IntelGpu.Controls;
using WSGM.Plugin.IntelGpu.Igcl;

namespace WSGM.Plugin.IntelGpu.Display;

/// <summary>One field of <c>ctl_intel_arc_sync_profile_params_t</c> that a Custom profile row publishes.</summary>
internal sealed class ArcSyncField
{
    public static readonly ArcSyncField MinimumHz = new(
        "display.arc-sync-min-refresh",
        "Custom minimum refresh (Hz)",
        true,
        static profile => profile.MinimumHz,
        static (ref profile, value) => profile.MinimumHz = value);

    public static readonly ArcSyncField MaximumHz = new(
        "display.arc-sync-max-refresh",
        "Custom maximum refresh (Hz)",
        true,
        static profile => profile.MaximumHz,
        static (ref profile, value) => profile.MaximumHz = value);

    public static readonly ArcSyncField FrameTimeIncrease = new(
        "display.arc-sync-frame-time-increase",
        "Custom max frame time increase (µs)",
        false,
        static profile => profile.MaxFrameTimeIncreaseUs,
        static (ref profile, value) => profile.MaxFrameTimeIncreaseUs = (uint)value);

    public static readonly ArcSyncField FrameTimeDecrease = new(
        "display.arc-sync-frame-time-decrease",
        "Custom max frame time decrease (µs)",
        false,
        static profile => profile.MaxFrameTimeDecreaseUs,
        static (ref profile, value) => profile.MaxFrameTimeDecreaseUs = (uint)value);

    private readonly Func<CtlArcSyncProfileParams, double> _get;
    private readonly Setter _set;

    private ArcSyncField(string id, string label, bool refresh, Func<CtlArcSyncProfileParams, double> get, Setter set)
    {
        Id = id;
        Label = label;
        IsRefresh = refresh;
        _get = get;
        _set = set;
    }

    /// <summary>Every field, in row order.</summary>
    public static IReadOnlyList<ArcSyncField> All { get; } =
        [MinimumHz, MaximumHz, FrameTimeIncrease, FrameTimeDecrease];

    /// <summary>The row's capability id.</summary>
    public string Id { get; }

    /// <summary>The row's label.</summary>
    public string Label { get; }

    /// <summary>Whether the field is a refresh rate in Hz rather than a frame time change in microseconds.</summary>
    public bool IsRefresh { get; }

    /// <summary>The field's raw value.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>The value in the field's own unit.</returns>
    public double Get(CtlArcSyncProfileParams profile)
    {
        return _get(profile);
    }

    /// <summary>Sets the field.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="value">The value in the field's own unit.</param>
    public void Set(ref CtlArcSyncProfileParams profile, int value)
    {
        _set(ref profile, value);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Id;
    }

    private delegate void Setter(ref CtlArcSyncProfileParams profile, int value);
}

/// <summary>
///     The bounds a Custom Arc Sync profile may use on one monitor, from
///     <c>ctlGetIntelArcSyncInfoForMonitor</c>.
/// </summary>
/// <param name="MinimumHz">The lowest whole refresh rate inside the monitor's range.</param>
/// <param name="MaximumHz">The highest whole refresh rate inside the monitor's range.</param>
/// <param name="FrameTimeMaximum">The largest frame time change offered, in microseconds.</param>
internal readonly record struct ArcSyncBounds(int MinimumHz, int MaximumHz, int FrameTimeMaximum)
{
    /// <summary>Derives the bounds from what the monitor reports.</summary>
    /// <param name="monitor">The monitor parameters.</param>
    /// <returns>The bounds, or null when the range holds fewer than two whole refresh rates.</returns>
    /// <remarks>
    ///     Intel's sample says the Custom refresh rates must lie in the panel's supported range. The frame
    ///     time changes are bounded by the whole frame time span of that range, since no step between two
    ///     frames can exceed it, and never below what the monitor itself reports.
    /// </remarks>
    public static ArcSyncBounds? From(CtlArcSyncMonitorParams monitor)
    {
        if (!float.IsFinite(monitor.MinimumHz) || !float.IsFinite(monitor.MaximumHz) || monitor.MinimumHz <= 0)
        {
            return null;
        }

        var minimum = (int)Math.Ceiling(monitor.MinimumHz - 0.001);
        var maximum = (int)Math.Floor(monitor.MaximumHz + 0.001);
        if (maximum <= minimum)
        {
            return null;
        }

        var span = (long)Math.Ceiling(1_000_000.0 / minimum - 1_000_000.0 / maximum - 0.001);
        var frameTime = Math.Max(span, Math.Max(monitor.MaxFrameTimeIncreaseUs, monitor.MaxFrameTimeDecreaseUs));
        return new ArcSyncBounds(minimum, maximum, (int)Math.Clamp(frameTime, 1, 1_000_000));
    }

    /// <summary>The range one field's row offers.</summary>
    /// <param name="field">The field.</param>
    /// <returns>Inclusive minimum and maximum.</returns>
    public (int Minimum, int Maximum) RangeOf(ArcSyncField field)
    {
        return field.IsRefresh ? (MinimumHz, MaximumHz) : (0, FrameTimeMaximum);
    }

    /// <summary>One field of a profile, as the row publishes it: rounded and inside the row's range.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="field">The field.</param>
    /// <returns>The value.</returns>
    public int Read(CtlArcSyncProfileParams profile, ArcSyncField field)
    {
        var (minimum, maximum) = RangeOf(field);
        var value = field.Get(profile);
        return double.IsFinite(value)
            ? (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), minimum, maximum)
            : minimum;
    }

    /// <summary>Sets one field of a profile.</summary>
    /// <param name="profile">The profile the other fields come from.</param>
    /// <param name="field">The field.</param>
    /// <param name="value">The value, already inside the row's range.</param>
    /// <returns>The profile with that field set.</returns>
    public static CtlArcSyncProfileParams Apply(CtlArcSyncProfileParams profile, ArcSyncField field, int value)
    {
        var result = profile;
        field.Set(ref result, value);
        return result;
    }

    /// <summary>Brings a profile's refresh rates inside the monitor's range, keeping them ordered.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>The profile with usable refresh rates.</returns>
    public CtlArcSyncProfileParams Clamp(CtlArcSyncProfileParams profile)
    {
        var result = profile;
        result.MinimumHz = Read(profile, ArcSyncField.MinimumHz);
        result.MaximumHz = Read(profile, ArcSyncField.MaximumHz);
        if (result.MinimumHz >= result.MaximumHz)
        {
            result.MinimumHz = MinimumHz;
            result.MaximumHz = MaximumHz;
        }

        result.MaxFrameTimeIncreaseUs = (uint)Read(profile, ArcSyncField.FrameTimeIncrease);
        result.MaxFrameTimeDecreaseUs = (uint)Read(profile, ArcSyncField.FrameTimeDecrease);
        return result;
    }
}

/// <summary>
///     One display's Intel Arc Sync state: the profile, and the profile to bring back when variable
///     refresh is turned on again.
/// </summary>
/// <remarks>
///     Carried over from the Claw package, where it is hardware-verified on the Claw 8 AI+ A2VM on
///     2026-08-30: the panel reports support across 30-120 Hz, a write to OFF and a restore of the saved
///     parameter structure both succeed, and the readback confirms each. Enabling restores the last
///     profile other than OFF, so a user who chose EXCELLENT does not end up on RECOMMENDED after a
///     toggle; only when nothing was seen does it fall back to the driver's own RECOMMENDED. Unlike the
///     Claw's version nothing is rolled back: a write is graded by its readback and never undone.
///     <para>
///         The Custom profile (<c>CTL_INTEL_ARC_SYNC_PROFILE_CUSTOM</c>, <c>igcl_api.h</c> line 3933) carries
///         its own refresh range and frame time limits in the same structure. That path follows
///         <c>Samples/IntelArcSync</c> and is blind.
///     </para>
/// </remarks>
internal sealed class ArcSyncDisplay
{
    public const int ProfileRecommended = 1;
    public const int ProfileExcellent = 2;
    public const int ProfileGood = 3;
    public const int ProfileCompatible = 4;
    public const int ProfileOff = 5;
    public const int ProfileVesa = 6;
    public const int ProfileCustom = 7;

    private readonly IgclSource<CtlArcSyncProfileParams> _profile;
    private CtlArcSyncProfileParams _custom;
    private bool _customKnown;
    private CtlArcSyncProfileParams _restore;
    private bool _restoreKnown;

    private ArcSyncDisplay(IgclSource<CtlArcSyncProfileParams> profile, CtlArcSyncMonitorParams monitor)
    {
        _profile = profile;
        Monitor = monitor;
        Bounds = ArcSyncBounds.From(monitor);
    }

    /// <summary>What the monitor reports.</summary>
    public CtlArcSyncMonitorParams Monitor { get; }

    /// <summary>What a Custom profile may use, or null when the monitor's range does not allow one.</summary>
    public ArcSyncBounds? Bounds { get; }

    /// <summary>Builds the state when the monitor supports Arc Sync.</summary>
    /// <param name="session">The session.</param>
    /// <param name="output">The output.</param>
    /// <param name="log">Receives the decision.</param>
    /// <param name="name">The display name for the trace.</param>
    /// <returns>The state, or null when variable refresh is not offered.</returns>
    public static unsafe ArcSyncDisplay? TryCreate(IgclSession session, IgclOutput output, IntelLog log, string name)
    {
        var api = session.Api;
        if (api.GetArcSyncInfo is null || api.GetArcSyncProfile is null || api.SetArcSyncProfile is null)
        {
            return null;
        }

        CtlArcSyncMonitorParams monitor = default;
        var result = session.Call(api.GetArcSyncInfo, output.Handle, ref monitor);
        if (result != IgclResult.Success || monitor.IsSupported == 0)
        {
            log.Info("arcsync", $"{name}: no variable refresh ({IgclResult.Describe(result)}).");
            return null;
        }

        ArcSyncDisplay display = new(
            new IgclSource<CtlArcSyncProfileParams>(session, output.Handle, api.GetArcSyncProfile,
                api.SetArcSyncProfile, default),
            monitor);
        _ = display.ReadProfile(out _);
        log.Info(
            "arcsync",
            $"{name}: variable refresh {monitor.MinimumHz:0}-{monitor.MaximumHz:0} Hz, frame time limits "
            + $"+{monitor.MaxFrameTimeIncreaseUs}/-{monitor.MaxFrameTimeDecreaseUs} us"
            + (display.Bounds is null ? "; the range is too narrow for a Custom profile." : "."));
        return display;
    }

    /// <summary>Reads the profile in use, once per pass, remembering what enabling and Custom start from.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>The driver result.</returns>
    public int ReadProfile(out CtlArcSyncProfileParams profile)
    {
        var result = _profile.Read(out profile);
        if (result == IgclResult.Success)
        {
            Remember(profile);
        }

        return result;
    }

    /// <summary>Writes variable refresh on or off.</summary>
    /// <param name="enabled">The request.</param>
    /// <returns>The driver result.</returns>
    public int WriteEnabled(bool enabled)
    {
        // The read only captures the profile to bring back; the write happens either way.
        _ = ReadProfile(out _);
        var request = _restoreKnown ? _restore : default;
        request.Profile = enabled
            ? _restoreKnown ? _restore.Profile : ProfileRecommended
            : ProfileOff;
        return Set(request);
    }

    /// <summary>Writes a named profile, or Custom with the last custom values seen.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>The driver result.</returns>
    public int WriteProfile(int profile)
    {
        var request = profile == ProfileCustom ? CustomBase() : MonitorProfile();
        request.Profile = profile;
        return Set(request);
    }

    /// <summary>Writes one value of the Custom profile, carrying the others.</summary>
    /// <param name="field">The field.</param>
    /// <param name="value">The value, inside the row's range.</param>
    /// <returns>How the driver answered.</returns>
    /// <remarks>
    ///     The other fields come from the current profile when it is Custom, else from the last Custom
    ///     values seen. The write sets the Custom profile either way; the read never decides whether to
    ///     write.
    /// </remarks>
    public ControlWrite WriteCustom(ArcSyncField field, int value)
    {
        if (Bounds is null)
        {
            return ControlWrite.Refuse("The monitor's range does not allow a Custom profile.");
        }

        var current = ReadProfile(out var profile) == IgclResult.Success && profile.Profile == ProfileCustom
            ? profile
            : CustomBase();
        var request = ArcSyncBounds.Apply(current, field, value);
        if (request.MinimumHz >= request.MaximumHz)
        {
            return ControlWrite.Refuse(
                $"The minimum refresh ({request.MinimumHz:0} Hz) must stay below the maximum ({request.MaximumHz:0} Hz).");
        }

        request.Profile = ProfileCustom;
        return ControlWrite.From(Set(request), $"the Custom Arc Sync {field}");
    }

    /// <summary>What a switch to Custom starts from: the last Custom values, else what the driver uses now.</summary>
    private CtlArcSyncProfileParams CustomBase()
    {
        CtlArcSyncProfileParams start;
        if (_customKnown)
        {
            start = _custom;
        }
        else if (ReadProfile(out var current) == IgclResult.Success && current.Profile != ProfileOff)
        {
            // The driver reports the range and limits it applies for the named profile in use.
            start = current;
        }
        else
        {
            start = MonitorProfile();
        }

        return Bounds is { } bounds ? bounds.Clamp(start) : start;
    }

    /// <summary>A profile carrying the monitor's own range and frame time limits.</summary>
    private CtlArcSyncProfileParams MonitorProfile()
    {
        CtlArcSyncProfileParams profile = default;
        profile.MinimumHz = Monitor.MinimumHz;
        profile.MaximumHz = Monitor.MaximumHz;
        profile.MaxFrameTimeIncreaseUs = Monitor.MaxFrameTimeIncreaseUs;
        profile.MaxFrameTimeDecreaseUs = Monitor.MaxFrameTimeDecreaseUs;
        return profile;
    }

    private int Set(CtlArcSyncProfileParams request)
    {
        var result = _profile.Write(request);
        if (result == IgclResult.Success)
        {
            Remember(request);
        }

        return result;
    }

    private void Remember(CtlArcSyncProfileParams profile)
    {
        if (profile.Profile == ProfileCustom)
        {
            _custom = profile;
            _customKnown = true;
        }

        // A profile of OFF says nothing about what the user would want when enabling.
        if (profile.Profile is ProfileOff or <= 0 or > ProfileCustom)
        {
            return;
        }

        _restore = profile;
        _restoreKnown = true;
    }
}

/// <summary>Variable refresh on or off for one display.</summary>
internal sealed class VariableRefreshControl : IntelControl
{
    private readonly ArcSyncDisplay _display;

    public VariableRefreshControl(ArcSyncDisplay display, string instance, bool semantic, Placement placement)
        : base(Descriptors.Toggle(
            "display.variable-refresh",
            instance,
            "Variable refresh rate",
            placement,
            semantic ? CapabilityRole.VariableRefreshRate : CapabilityRole.GenericToggle))
    {
        _display = display;
    }

    /// <inheritdoc />
    public override ControlRead Read()
    {
        var result = _display.ReadProfile(out var profile);
        return result == IgclResult.Success
            ? ControlRead.Of(Boolean(profile.Profile != ArcSyncDisplay.ProfileOff))
            : ControlRead.Driver(result);
    }

    /// <inheritdoc />
    protected override ControlWrite WriteValidated(CapabilityValue value)
    {
        return ControlWrite.From(_display.WriteEnabled(value.BooleanValue == true), "variable refresh");
    }
}

/// <summary>The Arc Sync profile of one display, offered while variable refresh is on.</summary>
internal sealed class ArcSyncProfileControl : IntelControl
{
    /// <summary>Why the profile and Custom rows cannot be used while variable refresh is off.</summary>
    internal static readonly CapabilityReason VariableRefreshOff =
        new(CapabilityReasonCode.PrerequisiteMissing, "Variable refresh is off.");

    private static readonly EnumMember[] Named =
    [
        new(ArcSyncDisplay.ProfileRecommended, "recommended", "Recommended"),
        new(ArcSyncDisplay.ProfileExcellent, "excellent", "Excellent"),
        new(ArcSyncDisplay.ProfileGood, "good", "Good"),
        new(ArcSyncDisplay.ProfileCompatible, "compatible", "Compatible"),
        new(ArcSyncDisplay.ProfileVesa, "vesa", "VESA")
    ];

    private static readonly EnumMember[] WithCustom = [.. Named, new(ArcSyncDisplay.ProfileCustom, "custom", "Custom")];

    private readonly ArcSyncDisplay _display;

    public ArcSyncProfileControl(ArcSyncDisplay display, string instance, Placement placement)
        : this(display, instance, placement, Profiles(display.Bounds is not null))
    {
    }

    private ArcSyncProfileControl(
        ArcSyncDisplay display,
        string instance,
        Placement placement,
        IReadOnlyList<EnumMember> profiles)
        : base(Descriptors.Choice("display.arc-sync-profile", instance, "Arc Sync profile", profiles, placement),
            profiles)
    {
        _display = display;
    }

    /// <summary>The offered profiles: the named ones, and Custom when the monitor's range allows it.</summary>
    /// <param name="custom">Whether Custom is offered.</param>
    /// <returns>The profiles in offer order.</returns>
    internal static IReadOnlyList<EnumMember> Profiles(bool custom)
    {
        return custom ? WithCustom : Named;
    }

    /// <inheritdoc />
    public override ControlRead Read()
    {
        var result = _display.ReadProfile(out var profile);
        if (result != IgclResult.Success)
        {
            return ControlRead.Driver(result);
        }

        return profile.Profile == ArcSyncDisplay.ProfileOff
            ? ControlRead.Unavailable(VariableRefreshOff)
            : ControlRead.Of(profile.Profile < 0 ? null : MemberOf((uint)profile.Profile));
    }

    /// <inheritdoc />
    protected override ControlWrite WriteValidated(CapabilityValue value)
    {
        return ControlWrite.From(_display.WriteProfile((int)ValueOf(value)), $"Arc Sync profile {value.ChoiceValue}");
    }
}

/// <summary>One value of a display's Custom Arc Sync profile, available while that profile is in use.</summary>
internal sealed class ArcSyncParameterControl : IntelControl
{
    private static readonly CapabilityReason NotCustom =
        new(CapabilityReasonCode.PrerequisiteMissing, "Only the Custom Arc Sync profile takes its own values.");

    private readonly ArcSyncBounds _bounds;
    private readonly ArcSyncDisplay _display;
    private readonly ArcSyncField _field;

    private ArcSyncParameterControl(
        ArcSyncDisplay display,
        ArcSyncBounds bounds,
        ArcSyncField field,
        CapabilityDescriptor descriptor)
        : base(descriptor)
    {
        _display = display;
        _bounds = bounds;
        _field = field;
    }

    /// <summary>Builds the four Custom profile rows, when the monitor's range allows a Custom profile.</summary>
    /// <param name="display">The display's Arc Sync state.</param>
    /// <param name="instance">The display's instance id.</param>
    /// <param name="placement">Where the first row sits; the others follow it.</param>
    /// <returns>The rows, possibly none.</returns>
    public static IReadOnlyList<ArcSyncParameterControl> Build(ArcSyncDisplay display, string instance,
        Placement placement)
    {
        if (display.Bounds is not { } bounds)
        {
            return [];
        }

        List<ArcSyncParameterControl> rows = [];
        foreach (var field in ArcSyncField.All)
        {
            var (minimum, maximum) = bounds.RangeOf(field);
            rows.Add(new ArcSyncParameterControl(display, bounds, field,
                Descriptors.Range(field.Id, instance, field.Label, IntegerRange.Linear(minimum, maximum),
                    CapabilityUnit.None, placement.Plus(rows.Count))));
        }

        return rows;
    }

    /// <inheritdoc />
    public override ControlRead Read()
    {
        var result = _display.ReadProfile(out var profile);
        if (result != IgclResult.Success)
        {
            return ControlRead.Driver(result);
        }

        // The value is what the driver applies now, shown even while the row cannot be changed.
        var value = CapabilityValue.Integer(_bounds.Read(profile, _field));
        return profile.Profile switch
        {
            ArcSyncDisplay.ProfileCustom => ControlRead.Of(value),
            ArcSyncDisplay.ProfileOff => ControlRead.Unavailable(ArcSyncProfileControl.VariableRefreshOff, value),
            _ => ControlRead.Unavailable(NotCustom, value)
        };
    }

    /// <inheritdoc />
    protected override ControlWrite WriteValidated(CapabilityValue value)
    {
        return _display.WriteCustom(_field, value.IntegerValue!.Value);
    }
}
