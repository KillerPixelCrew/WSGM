using System;
using System.IO;

namespace WSGM.Core;

/// <summary>The two bounded RTSS controls exposed through shared performance state.</summary>
internal enum PerformanceControl
{
    /// <summary>RTSS frame limit in frames per second; zero removes the limit.</summary>
    FrameLimit,

    /// <summary>WSGM OSD selector: off, three presets, or the custom layout.</summary>
    OverlayLevel
}

/// <summary>Lifecycle of the last semantic performance command.</summary>
internal enum PerformanceCommandPhase
{
    /// <summary>No command has been submitted.</summary>
    Idle,

    /// <summary>The command is awaiting serialized adapter access.</summary>
    Queued,

    /// <summary>The command is validating, persisting, or writing its value.</summary>
    Applying,

    /// <summary>The preference is saved while the application executable is still unknown.</summary>
    Deferred,

    /// <summary>The adapter accepted the write; immediate readback is not required.</summary>
    Applied,

    /// <summary>The command was refused by admission, validation, or the adapter.</summary>
    Rejected,

    /// <summary>Persistence or processing failed, timed out, or was cancelled after admission.</summary>
    Failed,

    /// <summary>Polling detected a value different from the previous observation.</summary>
    ExternalChange
}

/// <summary>Canonical WSGM application identity plus optional Steam and RTSS enrichment.</summary>
/// <remarks>
///     <see cref="ApplicationId" /> is authoritative whenever this record exists. Steam can name a game
///     before Windows exposes its foreground executable, so <see cref="RtssProfileName" /> is optional:
///     policy remains per-application. The frame limit waits for enrichment; the overlay level
///     applies through the global RTSS profile meanwhile.
/// </remarks>
/// <param name="ApplicationId">Authoritative WSGM application identifier.</param>
/// <param name="SteamAppId">Steam identity, when known.</param>
/// <param name="RtssProfileName">Executable profile name, or null until executable identity is available.</param>
/// <param name="ProcessId">Known application process, or null.</param>
internal sealed record PerformanceApplicationTarget(
    string ApplicationId,
    uint? SteamAppId,
    string? RtssProfileName,
    int? ProcessId = null)
{
    /// <summary>The RTSS target for the running application, or null for the global profile.</summary>
    /// <param name="active">The running application.</param>
    /// <returns>The target, with an invalid executable dropped.</returns>
    internal static PerformanceApplicationTarget? For(ActiveProfile active)
    {
        if (!active.HasApplication)
        {
            return null;
        }

        var executable = active.Executable is { } name && ValidProfileName(name) ? name.Trim() : null;
        return new PerformanceApplicationTarget(active.ApplicationId!.Trim(), active.SteamAppId, executable);
    }

    private static bool ValidProfileName(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
               && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Desired or observed values. Null means the corresponding control has no value.</summary>
/// <param name="FrameLimit">Frame limit in frames per second; zero is unlimited, null is unspecified.</param>
/// <param name="OverlayLevel">WSGM overlay selector value, or null when unspecified.</param>
internal sealed record PerformanceValues(int? FrameLimit, int? OverlayLevel)
{
    /// <summary>No value for either control.</summary>
    internal static readonly PerformanceValues Empty = new(null, null);

    /// <summary>Returns the value for one control.</summary>
    /// <param name="control">The control to select.</param>
    /// <returns>The stored value, or null for an unknown control.</returns>
    internal int? ValueFor(PerformanceControl control)
    {
        return control switch
        {
            PerformanceControl.FrameLimit => FrameLimit,
            PerformanceControl.OverlayLevel => OverlayLevel,
            _ => null
        };
    }

    /// <summary>Returns a copy with one control changed.</summary>
    /// <param name="control">The control to change.</param>
    /// <param name="value">The replacement value; this method does not validate bounds.</param>
    /// <returns>The updated copy, or this instance for an unknown control.</returns>
    internal PerformanceValues With(PerformanceControl control, int value)
    {
        return control switch
        {
            PerformanceControl.FrameLimit => this with { FrameLimit = value },
            PerformanceControl.OverlayLevel => this with { OverlayLevel = value },
            _ => this
        };
    }
}

/// <summary>Immutable command status shared by every performance UI client.</summary>
/// <param name="Sequence">Monotonically increasing service command identifier; zero is the idle sentinel.</param>
/// <param name="Origin">Sanitized name of the submitting surface or workflow.</param>
/// <param name="CorrelationId">Sanitized request token for diagnostics.</param>
/// <param name="Control">Control addressed by this command.</param>
/// <param name="RequestedValue">Requested value, or null when no command exists.</param>
/// <param name="Phase">Current processing outcome or stage.</param>
/// <param name="Diagnostic">Explanation of the stage or failure, when available.</param>
internal sealed record PerformanceCommandState(
    long Sequence,
    string Origin,
    string CorrelationId,
    PerformanceControl Control,
    int? RequestedValue,
    PerformanceCommandPhase Phase,
    string? Diagnostic)
{
    /// <summary>Initial state before any command is submitted.</summary>
    internal static readonly PerformanceCommandState Idle = new(
        0,
        string.Empty,
        string.Empty,
        PerformanceControl.FrameLimit,
        null,
        PerformanceCommandPhase.Idle,
        null);
}

/// <summary>Immutable RTSS state projected into the overlay and native QAM.</summary>
/// <remarks>
///     <see cref="Observed" /> is what RTSS last read back, or the value WSGM last wrote there, and
///     empty while RTSS is unavailable. An unknown executable leaves the frame limit unobserved,
///     while the overlay level comes from the adapter's WSGM renderer state.
/// </remarks>
/// <param name="Probe">Latest discovery and adapter availability result.</param>
/// <param name="Target">Running application identity, or null for the global target.</param>
/// <param name="ApplicationProfileEnabled">Whether the active application uses its own WSGM profile.</param>
/// <param name="FrameLimitLayer">Profile layer supplying the resolved frame limit.</param>
/// <param name="OverlayLevelLayer">Profile layer supplying the resolved overlay level.</param>
/// <param name="Desired">Values resolved from the current WSGM profile policy.</param>
/// <param name="Observed">Last values read or successfully written; not proof of hardware readback.</param>
/// <param name="RefreshedAt">Time of the last observation, published write, or unavailable refresh.</param>
/// <param name="Command">Most recent semantic command status.</param>
/// <param name="OverlayPresentationEnabled">RTSS's presentation gates; separate from the selected overlay level.</param>
internal sealed record PerformanceState(
    RtssProbe Probe,
    PerformanceApplicationTarget? Target,
    bool ApplicationProfileEnabled,
    ProfileSource FrameLimitLayer,
    ProfileSource OverlayLevelLayer,
    PerformanceValues Desired,
    PerformanceValues Observed,
    DateTimeOffset? RefreshedAt,
    PerformanceCommandState Command,
    bool? OverlayPresentationEnabled = null);

/// <summary>The RTSS wording and frame-limit bounds the overlay and Steam's Quick Access both show.</summary>
internal static class PerformanceProjection
{
    /// <summary>The highest frame cap either slider offers.</summary>
    /// <remarks>
    ///     280 rather than whatever RTSS reports it will accept (1000). The slider has to be crossable
    ///     on a thumbstick, and a range that reaches a thousand makes every rate anyone actually uses
    ///     live in its first third. This covers every panel a handheld drives, internal or attached.
    /// </remarks>
    internal const int MaximumFrameLimit = 280;

    /// <summary>Why RTSS cannot be used in this state, or null when the probe gives no fixed reason.</summary>
    /// <param name="availability">The probe's availability.</param>
    /// <returns>The sentence for an unavailable state; null for Ready and for states that carry their own diagnostic.</returns>
    internal static string? UnavailableText(RtssAvailability availability)
    {
        return availability switch
        {
            RtssAvailability.Unknown => "Checking RTSS…",
            RtssAvailability.NotInstalled => "RTSS is not installed.",
            RtssAvailability.NotRunning => "RTSS is not running.",
            RtssAvailability.Incompatible => "The installed RTSS version is not supported.",
            RtssAvailability.AdapterUnavailable => "The RTSS profile API is unavailable.",
            _ => null
        };
    }

    /// <summary>Which RTSS profile the controls currently edit.</summary>
    /// <param name="target">The resolved application target, or null for Global.</param>
    /// <returns>The profile line.</returns>
    internal static string TargetText(PerformanceApplicationTarget? target)
    {
        return target switch
        {
            null => "RTSS · global profile",
            { RtssProfileName: { Length: > 0 } profile } => $"RTSS · {profile}",
            { SteamAppId: { } appId } => $"Steam AppID {appId} · executable pending",
            _ => "Foreground application · executable pending"
        };
    }

    /// <summary>The frame-cap bounds a slider offers.</summary>
    /// <param name="capabilities">What this RTSS build accepts.</param>
    /// <param name="panel">The lowest and highest cap the panel's refresh rates suggest, or null when unknown.</param>
    /// <returns>
    ///     The panel's band within what RTSS accepts and <see cref="MaximumFrameLimit" />, with whether it came
    ///     from the panel; without a usable panel band, RTSS's own floor up to the same ceiling.
    /// </returns>
    internal static (int Minimum, int Maximum, bool FromPanel) FrameLimitBounds(RtssCapabilities capabilities,
        (int Minimum, int Maximum)? panel)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var ceiling = Math.Min(MaximumFrameLimit, capabilities.MaximumFrameLimit);
        if (panel is { } band)
        {
            var floor = Math.Max(capabilities.MinimumFrameLimit, band.Minimum);
            var top = Math.Min(band.Maximum, ceiling);
            if (top >= floor)
            {
                return (floor, top, true);
            }
        }

        return (Math.Max(0, capabilities.MinimumFrameLimit), ceiling, false);
    }
}
