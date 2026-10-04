using System.Text.Json.Serialization;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Where a capability's UI is in the request cycle.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CommandProgress>))]
public enum CommandProgress
{
    /// <summary>Nothing is in flight.</summary>
    Idle,

    /// <summary>A command has been sent and no result has come back.</summary>
    Pending,

    /// <summary>The last command finished cleanly.</summary>
    Completed,

    /// <summary>The last command failed or was refused.</summary>
    Failed,

    /// <summary>The last command finished without establishing what the hardware did.</summary>
    Uncertain
}

/// <summary>
///     WSGM's view of one capability: what the plugin last reported, plus what WSGM wants and is doing
///     about it.
/// </summary>
/// <remarks>
///     The split from <see cref="CapabilityState" /> is the point. The plugin owns observation; WSGM owns
///     intent. Keeping intent out of the plugin's message is what stops a device that happens to boot at
///     15 W from being treated as though the user chose 15 W.
/// </remarks>
public sealed record CapabilityProjection
{
    /// <summary>The last state the plugin reported.</summary>
    public required CapabilityState State { get; init; }

    /// <summary>The value WSGM wants, or null when no layer supplies one.</summary>
    public CapabilityValue? DesiredValue { get; init; }

    /// <summary>Which layer supplied <see cref="DesiredValue" />.</summary>
    public ProfileSource DesiredSource { get; init; } = ProfileSource.None;

    /// <summary>The Global layer's value, or null when Global does not set one.</summary>
    /// <remarks>
    ///     What WSGM writes through commands for a <see cref="CapabilityProfileScope.GlobalOnly" /> or
    ///     <see cref="CapabilityProfileScope.NativePerApplication" /> capability. For the second,
    ///     <see cref="DesiredValue" /> still shows the running game's own value, which its driver applies.
    /// </remarks>
    public CapabilityValue? GlobalDesiredValue { get; init; }

    /// <summary>How the descriptor carries a remembered value between games.</summary>
    public CapabilityProfileScope ProfileScope { get; init; } = CapabilityProfileScope.Switched;

    /// <summary>When a written value takes effect, for "applies next game start" or "restart required".</summary>
    public CapabilityApplyTiming ApplyTiming { get; init; } = CapabilityApplyTiming.Immediate;

    /// <summary>The value of an in-flight request, shown while a command is pending.</summary>
    public CapabilityValue? PendingValue { get; init; }

    /// <summary>Where the UI is in the request cycle.</summary>
    public CommandProgress Progress { get; init; } = CommandProgress.Idle;

    /// <summary>
    ///     Whether the desired value is outside what the current descriptor accepts.
    /// </summary>
    /// <remarks>
    ///     Set after a descriptor generation change narrowed a range. The persisted value is kept rather
    ///     than clamped: silently moving a user's 30 W request to 25 W because firmware changed would be
    ///     a decision made on their behalf and never surfaced.
    /// </remarks>
    public bool DesiredValueOutOfRange { get; init; }

    /// <summary>The setting id a control carries while the running game's profile supplies its value.</summary>
    /// <param name="layers">The profile layers, or null when there is no profile owner.</param>
    /// <param name="key">The setting the control shows.</param>
    /// <returns>The id that marks the control as the game's, or null when the value is not the game's.</returns>
    internal static string? OverrideId(ProfileLayers? layers, ProfileSettingKey key)
    {
        return layers?.Source(key) is ProfileSource.Game ? key.Id : null;
    }

    /// <summary>The setting id of a device capability while the running game's profile supplies it.</summary>
    /// <param name="view">The capability and its projection.</param>
    /// <returns>The id that marks the control as the game's, or null.</returns>
    internal static string? DeviceOverrideId(DeviceCapabilityView view)
    {
        return view.Projection.DesiredSource is ProfileSource.Game
            ? view.SettingKey.Id
            : null;
    }

    /// <summary>An integer value that lies on a descriptor's range and step, or null.</summary>
    /// <param name="value">The value to check.</param>
    /// <param name="minimum">The lowest allowed value.</param>
    /// <param name="maximum">The highest allowed value.</param>
    /// <param name="step">The step from <paramref name="minimum" />.</param>
    /// <returns>The integer, or null when it is missing or off the range.</returns>
    internal static int? ValidInteger(CapabilityValue? value, int minimum, int maximum, int step)
    {
        return value is { Kind: CapabilityValueKind.Integer, IntegerValue: { } integer }
               && integer >= minimum
               && integer <= maximum
               && (integer - minimum) % step == 0
            ? integer
            : null;
    }
}
