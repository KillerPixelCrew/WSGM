using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.IntelGpu.Controls;
using WSGM.Plugin.IntelGpu.Igcl;

namespace WSGM.Plugin.IntelGpu.Display;

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

    private readonly IgclOutput _output;
    private readonly IgclSession _session;
    private CtlArcSyncProfileParams _restore;
    private bool _restoreKnown;

    private ArcSyncDisplay(IgclSession session, IgclOutput output, CtlArcSyncMonitorParams monitor)
    {
        _session = session;
        _output = output;
        Monitor = monitor;
    }

    /// <summary>What the monitor reports.</summary>
    public CtlArcSyncMonitorParams Monitor { get; }

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

        log.Info("arcsync", $"{name}: variable refresh {monitor.MinimumHz:0}-{monitor.MaximumHz:0} Hz.");
        return display;
    }

    public int ReadProfile(out CtlArcSyncProfileParams profile)
    {
        CtlArcSyncProfileParams current = default;
        current.Size = (uint)sizeof(CtlArcSyncProfileParams);
        var result = _session.Observe(_session.Api.GetArcSyncProfile(_output.Handle, &current));
        profile = current;
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
        return _session.Observe(_session.Api.SetArcSyncProfile(_output.Handle, &request));
    }

    /// <summary>Writes a named profile.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>The driver result.</returns>
    public int WriteProfile(int profile)
    {
        CtlArcSyncProfileParams request = default;
        request.Size = (uint)sizeof(CtlArcSyncProfileParams);
        request.Profile = profile;
        request.MinimumHz = Monitor.MinimumHz;
        request.MaximumHz = Monitor.MaximumHz;
        request.MaxFrameTimeIncreaseUs = Monitor.MaxFrameTimeIncreaseUs;
        request.MaxFrameTimeDecreaseUs = Monitor.MaxFrameTimeDecreaseUs;
        var result = _session.Observe(_session.Api.SetArcSyncProfile(_output.Handle, &request));
        if (result == IgclResult.Success)
        {
            Remember(request);
        }

        return result;
    }

    private void Remember(CtlArcSyncProfileParams profile)
    {
        // A profile of OFF says nothing about what the user would want when enabling.
        if (profile.Profile is ProfileOff or <= 0 or >= 8)
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
    private static readonly (int Profile, string Id, string Label)[] Profiles =
    [
        (ArcSyncDisplay.ProfileRecommended, "recommended", "Recommended"),
        (ArcSyncDisplay.ProfileExcellent, "excellent", "Excellent"),
        (ArcSyncDisplay.ProfileGood, "good", "Good"),
        (ArcSyncDisplay.ProfileCompatible, "compatible", "Compatible"),
        (ArcSyncDisplay.ProfileVesa, "vesa", "VESA")
    ];

    private readonly ArcSyncDisplay _display;

    public ArcSyncProfileControl(ArcSyncDisplay display, string instance, Placement placement)
        : base(Descriptors.Choice(
            "display.arc-sync-profile",
            instance,
            "Arc Sync profile",
            [.. Profiles.Select(profile => (profile.Id, profile.Label))],
            placement))
    {
        _display = display;
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
                new CapabilityReason(CapabilityReasonCode.Unsupported, "Variable refresh is off."));
        }

        // A custom profile carries bounds this row does not offer, so it is reported as unknown
        // rather than as the nearest named profile.
        var match = Array.Find(Profiles, entry => entry.Profile == profile.Profile);
        return match.Id is null ? ControlRead.Failed(result) : ControlRead.Of(CapabilityValue.Choice(match.Id));
    }

    /// <inheritdoc />
    public override ControlWrite Write(CapabilityValue value)
    {
        var match = Array.Find(Profiles, entry => entry.Id == value.ChoiceValue);
        return ControlWrite.From(_display.WriteProfile(match.Profile), $"Arc Sync profile {match.Id}");
    }
}
