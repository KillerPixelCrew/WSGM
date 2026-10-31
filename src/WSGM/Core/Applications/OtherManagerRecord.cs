namespace WSGM.Core;

/// <summary>One manager startup, package activation or dedicated keyboard setting and how to put it back.</summary>
/// <remarks>
///     An install-lifecycle recovery snapshot like <see cref="SteamAutostartRecord" />: recorded before the
///     change, read by the uninstaller to undo exactly what WSGM changed, and removed once restored.
/// </remarks>
public sealed class OtherManagerRecord
{
    /// <summary>The manager it belongs to.</summary>
    public string ManagerId { get; set; } = "";

    /// <summary><c>service</c>, <c>task</c>, <c>package</c> or <c>device</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Service name or task path.</summary>
    public string Name { get; set; } = "";

    /// <summary>Service start type, or a package's prior disabled bit (0 enabled, 1 disabled).</summary>
    public int PreviousStart { get; set; }

    /// <summary>Whether a service started delayed before WSGM changed it.</summary>
    public bool PreviousDelayed { get; set; }
}
