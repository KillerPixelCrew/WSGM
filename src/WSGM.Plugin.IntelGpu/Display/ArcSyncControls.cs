using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.IntelGpu.Controls;
using WSGM.Plugin.IntelGpu.Igcl;

namespace WSGM.Plugin.IntelGpu.Display;

/// <summary>Which field of <c>ctl_intel_arc_sync_profile_params_t</c> a Custom profile row publishes.</summary>
internal enum ArcSyncField
{
    MinimumHz,
    MaximumHz,
    FrameTimeIncrease,
    FrameTimeDecrease
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
        return field is ArcSyncField.MinimumHz or ArcSyncField.MaximumHz
            ? (MinimumHz, MaximumHz)
            : (0, FrameTimeMaximum);
    }

    /// <summary>One field of a profile, as the row publishes it: rounded and inside the row's range.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="field">The field.</param>
    /// <returns>The value.</returns>
    public int Read(CtlArcSyncProfileParams profile, ArcSyncField field)
    {
        var (minimum, maximum) = RangeOf(field);
        double value = field switch
        {
            ArcSyncField.MinimumHz => profile.MinimumHz,
            ArcSyncField.MaximumHz => profile.MaximumHz,
            ArcSyncField.FrameTimeIncrease => profile.MaxFrameTimeIncreaseUs,
            _ => profile.MaxFrameTimeDecreaseUs
        };
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
        switch (field)
        {
            case ArcSyncField.MinimumHz:
                result.MinimumHz = value;
                break;
            case ArcSyncField.MaximumHz:
                result.MaximumHz = value;
                break;
            case ArcSyncField.FrameTimeIncrease:
                result.MaxFrameTimeIncreaseUs = (uint)value;
                break;
            default:
                result.MaxFrameTimeDecreaseUs = (uint)value;
                break;
        }

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
internal sealed unsafe class ArcSyncDisplay
{
    public const int ProfileRecommended = 1;
    public const int ProfileExcellent = 2;
    public const int ProfileGood = 3;
    public const int ProfileCompatible = 4;
    public const int ProfileOff = 5;
    public const int ProfileVesa = 6;
    public const int ProfileCustom = 7;

    /// <summary>How long one profile read serves the display's rows, which each read it in one pass.</summary>
    private const long ReadReuseMilliseconds = 500;

    private readonly IgclOutput _output;
    private readonly IgclSession _session;
    private CtlArcSyncProfileParams _custom;
    private bool _customKnown;
    private CtlArcSyncProfileParams _lastRead;
    private long _lastReadAt = long.MinValue;
    private int _lastReadResult;
    private CtlArcSyncProfileParams _restore;
    private bool _restoreKnown;

    private ArcSyncDisplay(IgclSession session, IgclOutput output, CtlArcSyncMonitorParams monitor)
    {
        _session = session;
        _output = output;
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
    public static ArcSyncDisplay? TryCreate(IgclSession session, IgclOutput output, IntelLog log, string name)
    {
        var api = session.Api;
        if (api.GetArcSyncInfo is null || api.GetArcSyncProfile is null || api.SetArcSyncProfile is null)
        {
            return null;
        }

        CtlArcSyncMonitorParams monitor = default;
        monitor.Size = (uint)sizeof(CtlArcSyncMonitorParams);
        var result = session.Observe(api.GetArcSyncInfo(output.Handle, &monitor));
        if (result != IgclResult.Success || monitor.IsSupported == 0)
        {
            log.Info("arcsync", $"{name}: no variable refresh ({IgclResult.Describe(result)}).");
            return null;
        }

        ArcSyncDisplay display = new(session, output, monitor);
        if (display.ReadProfile(out var profile) == IgclResult.Success)
        {
            display.Remember(profile);
        }

        log.Info(
            "arcsync",
            $"{name}: variable refresh {monitor.MinimumHz:0}-{monitor.MaximumHz:0} Hz, frame time limits "
            + $"+{monitor.MaxFrameTimeIncreaseUs}/-{monitor.MaxFrameTimeDecreaseUs} us"
            + (display.Bounds is null ? "; the range is too narrow for a Custom profile." : "."));
        return display;
    }

    public int ReadProfile(out CtlArcSyncProfileParams profile)
    {
        // Every row of the display reads the profile in one observation pass; one call serves them all.
        if (Environment.TickCount64 - _lastReadAt < ReadReuseMilliseconds)
        {
            profile = _lastRead;
            return _lastReadResult;
        }

        CtlArcSyncProfileParams current = default;
        current.Size = (uint)sizeof(CtlArcSyncProfileParams);
        var result = _session.Observe(_session.Api.GetArcSyncProfile(_output.Handle, &current));
        profile = current;
        _lastRead = current;
        _lastReadResult = result;
        _lastReadAt = Environment.TickCount64;
        if (result == IgclResult.Success)
        {
            Remember(current);
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
        request.Size = (uint)sizeof(CtlArcSyncProfileParams);
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
        CtlArcSyncProfileParams request;
        if (profile == ProfileCustom)
        {
            request = CustomBase();
        }
        else
        {
            request = default;
            request.MinimumHz = Monitor.MinimumHz;
            request.MaximumHz = Monitor.MaximumHz;
            request.MaxFrameTimeIncreaseUs = Monitor.MaxFrameTimeIncreaseUs;
            request.MaxFrameTimeDecreaseUs = Monitor.MaxFrameTimeDecreaseUs;
        }

        request.Size = (uint)sizeof(CtlArcSyncProfileParams);
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

        request.Size = (uint)sizeof(CtlArcSyncProfileParams);
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
            start = default;
            start.MinimumHz = Monitor.MinimumHz;
            start.MaximumHz = Monitor.MaximumHz;
            start.MaxFrameTimeIncreaseUs = Monitor.MaxFrameTimeIncreaseUs;
            start.MaxFrameTimeDecreaseUs = Monitor.MaxFrameTimeDecreaseUs;
        }

        return Bounds is { } bounds ? bounds.Clamp(start) : start;
    }

    private int Set(CtlArcSyncProfileParams request)
    {
        _lastReadAt = long.MinValue;
        var result = _session.Observe(_session.Api.SetArcSyncProfile(_output.Handle, &request));
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
            ? ControlRead.Of(CapabilityValue.Boolean(profile.Profile != ArcSyncDisplay.ProfileOff))
            : ControlRead.Failed(result);
    }

    /// <inheritdoc />
    public override ControlWrite Write(CapabilityValue value)
    {
        return ControlWrite.From(_display.WriteEnabled(value.BooleanValue == true), "variable refresh");
    }
}

/// <summary>The Arc Sync profile of one display, offered while variable refresh is on.</summary>
internal sealed class ArcSyncProfileControl : IntelControl
{
    private static readonly (int Profile, string Id, string Label)[] Named =
    [
        (ArcSyncDisplay.ProfileRecommended, "recommended", "Recommended"),
        (ArcSyncDisplay.ProfileExcellent, "excellent", "Excellent"),
        (ArcSyncDisplay.ProfileGood, "good", "Good"),
        (ArcSyncDisplay.ProfileCompatible, "compatible", "Compatible"),
        (ArcSyncDisplay.ProfileVesa, "vesa", "VESA")
    ];

    private static readonly (int Profile, string Id, string Label) Custom =
        (ArcSyncDisplay.ProfileCustom, "custom", "Custom");

    private readonly ArcSyncDisplay _display;
    private readonly (int Profile, string Id, string Label)[] _profiles;

    public ArcSyncProfileControl(ArcSyncDisplay display, string instance, Placement placement)
        : this(display, instance, placement, Profiles(display.Bounds is not null))
    {
    }

    private ArcSyncProfileControl(
        ArcSyncDisplay display,
        string instance,
        Placement placement,
        (int Profile, string Id, string Label)[] profiles)
        : base(Descriptors.Choice(
            "display.arc-sync-profile",
            instance,
            "Arc Sync profile",
            [.. profiles.Select(profile => (profile.Id, profile.Label))],
            placement))
    {
        _display = display;
        _profiles = profiles;
    }

    /// <summary>The offered profiles: the named ones, and Custom when the monitor's range allows it.</summary>
    /// <param name="custom">Whether Custom is offered.</param>
    /// <returns>The profiles in offer order.</returns>
    internal static (int Profile, string Id, string Label)[] Profiles(bool custom)
    {
        return custom ? [.. Named, Custom] : Named;
    }

    /// <inheritdoc />
    public override ControlRead Read()
    {
        var result = _display.ReadProfile(out var profile);
        if (result != IgclResult.Success)
        {
            return ControlRead.Failed(result);
        }

        if (profile.Profile == ArcSyncDisplay.ProfileOff)
        {
            return new ControlRead(
                null,
                result,
                false,
                new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing, "Variable refresh is off."));
        }

        var match = Array.Find(_profiles, entry => entry.Profile == profile.Profile);
        return match.Id is null ? ControlRead.Failed(result) : ControlRead.Of(CapabilityValue.Choice(match.Id));
    }

    /// <inheritdoc />
    public override ControlWrite Write(CapabilityValue value)
    {
        var match = Array.Find(_profiles, entry => entry.Id == value.ChoiceValue);
        return ControlWrite.From(_display.WriteProfile(match.Profile), $"Arc Sync profile {match.Id}");
    }
}

/// <summary>One value of a display's Custom Arc Sync profile, available while that profile is in use.</summary>
internal sealed class ArcSyncParameterControl : IntelControl
{
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
    public static IEnumerable<ArcSyncParameterControl> Build(ArcSyncDisplay display, string instance, Placement placement)
    {
        if (display.Bounds is not { } bounds)
        {
            yield break;
        }

        (ArcSyncField Field, string Id, string Label)[] rows =
        [
            (ArcSyncField.MinimumHz, "display.arc-sync-min-refresh", "Custom minimum refresh (Hz)"),
            (ArcSyncField.MaximumHz, "display.arc-sync-max-refresh", "Custom maximum refresh (Hz)"),
            (ArcSyncField.FrameTimeIncrease, "display.arc-sync-frame-time-increase",
                "Custom max frame time increase (µs)"),
            (ArcSyncField.FrameTimeDecrease, "display.arc-sync-frame-time-decrease",
                "Custom max frame time decrease (µs)")
        ];
        for (var index = 0; index < rows.Length; index++)
        {
            var (field, id, label) = rows[index];
            var (minimum, maximum) = bounds.RangeOf(field);
            yield return new ArcSyncParameterControl(display, bounds, field,
                Descriptors.Range(id, instance, label, minimum, maximum, 1, CapabilityUnit.None,
                    placement with { Order = placement.Order + index }));
        }
    }

    /// <inheritdoc />
    public override ControlRead Read()
    {
        var result = _display.ReadProfile(out var profile);
        if (result != IgclResult.Success)
        {
            return ControlRead.Failed(result);
        }

        // The value is what the driver applies now, shown even while the row cannot be changed.
        var value = CapabilityValue.Integer(_bounds.Read(profile, _field));
        return profile.Profile switch
        {
            ArcSyncDisplay.ProfileCustom => ControlRead.Of(value),
            ArcSyncDisplay.ProfileOff => new ControlRead(value, result, false,
                new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing, "Variable refresh is off.")),
            _ => new ControlRead(value, result, false,
                new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing,
                    "Only the Custom Arc Sync profile takes its own values."))
        };
    }

    /// <inheritdoc />
    public override ControlWrite Write(CapabilityValue value)
    {
        return _display.WriteCustom(_field, value.IntegerValue ?? 0);
    }
}
