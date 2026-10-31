using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Which per-application values a pass owes, and for which application it last carried each.</summary>
/// <remarks>
///     Keyed on what resolves rather than on the snapshot generation, and kept per capability: a key is
///     recorded only when its capability was published for the pass, so a capability that appears later
///     still reconciles on the next pass for the same application.
/// </remarks>
internal sealed class ApplicationReconcileKeys
{
    // Record value equality, not generated ToString text, decides whether a value changed.
    private (string? ApplicationId, ManualTdpProfile? Manual)? _power;
    private (string? ApplicationId, bool? VariableRefresh)? _variableRefresh;
    private (string? ApplicationId, CpuBoostMode? Mode)? _cpuBoost;

    /// <summary>Decides whether this pass carries the processor boost mode and records it as carried.</summary>
    /// <param name="applicationId">The running application, or null for the desktop.</param>
    /// <param name="mode">The mode the layers resolve to, or null.</param>
    /// <returns>Whether the mode differs from the one last carried for this application.</returns>
    /// <remarks>Windows policy is always published, so unlike the capability keys this one is always recorded.</remarks>
    internal bool TakeCpuBoost(string? applicationId, CpuBoostMode? mode)
    {
        (string? ApplicationId, CpuBoostMode? Mode) key = (applicationId, mode);
        if (_cpuBoost == key)
        {
            return false;
        }

        _cpuBoost = key;
        return true;
    }

    /// <summary>Decides which values this pass carries and records them as carried.</summary>
    /// <param name="applicationId">The running application, or null for the desktop.</param>
    /// <param name="manual">The power limit the layers resolve to, or null.</param>
    /// <param name="variableRefresh">The variable-refresh preference the layers resolve to, or null.</param>
    /// <param name="powerPublished">Whether a writable power limit is published right now.</param>
    /// <param name="variableRefreshPublished">Whether variable refresh is published right now.</param>
    /// <returns>Which values the pass reconciles.</returns>
    internal (bool Power, bool VariableRefresh) Take(
        string? applicationId,
        ManualTdpProfile? manual,
        bool? variableRefresh,
        bool powerPublished,
        bool variableRefreshPublished)
    {
        (string? ApplicationId, ManualTdpProfile? Manual) powerKey = (applicationId, manual);
        (string? ApplicationId, bool? VariableRefresh) variableRefreshKey = (applicationId, variableRefresh);
        var power = powerPublished && _power != powerKey;
        var refresh = variableRefreshPublished && _variableRefresh != variableRefreshKey;
        if (power)
        {
            _power = powerKey;
        }

        if (refresh)
        {
            _variableRefresh = variableRefreshKey;
        }

        return (power, refresh);
    }
}
