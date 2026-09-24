using System.Globalization;
using System.Linq;

namespace WSGM.Core;

/// <summary>Steam Input management as WSGM's settings surfaces apply and describe it.</summary>
/// <remarks>
///     WSGM Settings and WSGM's page in Steam both change <see cref="AppConfig.SteamInputManagementEnabled" />,
///     so both apply it the same way and say the same thing about the result. Applying follows persisted
///     intent and runs outside the config lock, because it may need an elevation prompt.
/// </remarks>
internal static class SteamInputManagement
{
    /// <summary>Deploys or parks the shim to match a saved configuration.</summary>
    /// <param name="config">The configuration that was just written.</param>
    /// <param name="reason">Why, for the log.</param>
    internal static void Apply(AppConfig config, string reason)
    {
        SteamInputShim.SetEnabled(config.SteamInputManagementEnabled);
        var status = SteamInputShim.Reconcile(reason);
        if (status is { State: SteamInputShimState.Failed, Detail: "access denied" }
            // Tri-state: only retry when we KNOW we are unelevated. Unknown stays
            // put rather than throwing a UAC prompt at a user who may not need one.
            && ElevationCheck.IsCurrentProcessElevated() == false)
        {
            // Steam normally lives under Program Files, which an unelevated process cannot
            // write. Without this the toggle would appear to do nothing at all on most machines.
            Log.Warn("Steam Input shim write refused - retrying elevated.");
            SelfElevation.RunElevatedAction(
                config.SteamInputManagementEnabled
                    ? "--apply-steam-input-shim"
                    : "--remove-steam-input-shim",
                "Steam Input shim");
            SteamInputShim.Probe();
        }

        if (!config.SteamInputManagementEnabled)
        {
            WarnAboutShimOnlyLaunchFixes(config);
        }
    }

    /// <summary>A plain-language description of the shim deployment.</summary>
    /// <param name="status">The deployment to describe.</param>
    /// <returns>One or two sentences, naming the file so a screenshot is diagnostic on its own.</returns>
    internal static string Describe(SteamInputShimStatus status)
    {
        var name = SteamInputShim.FileNameFor(status.Vector);
        return status.State switch
        {
            SteamInputShimState.SteamNotInstalled =>
                "Steam was not found on this PC, so nothing was installed.",
            SteamInputShimState.Disabled =>
                "Off. WSGM's file is parked next to Steam and does nothing; turning this back on restores it instantly.",
            SteamInputShimState.Deployed when SteamInputShim.LoadedVector is not null =>
                $"Active - installed as {name} and loaded by the running Steam.",
            SteamInputShimState.Deployed =>
                $"Installed as {name}. It takes effect the next time Steam starts.",
            SteamInputShimState.UpdatePending =>
                "An update is waiting: Steam is using the old copy right now. WSGM replaces it the next time it starts Steam.",
            SteamInputShimState.Blocked =>
                "Could not install: XInput1_4.dll and dinput8.dll in Steam's folder both belong to another program (ValvePlug or Special K, for example). WSGM will not overwrite them.",
            _ => "Could not write to Steam's folder. Run WSGM setup again, or start WSGM as administrator once."
        };
    }

    /// <summary>Names the games whose stored launch fix just stopped blocking.</summary>
    /// <remarks>
    ///     Turning Steam Input Management off changes what an already-written
    ///     <c>--input-lease</c> does: there is no resident shim left for it to use, so it
    ///     fails open. One log line is what makes "why did my controller fix stop working"
    ///     answerable from a pasted log instead of a bisect.
    /// </remarks>
    private static void WarnAboutShimOnlyLaunchFixes(AppConfig config)
    {
        var affected = config.LaunchWrappers
            .Where(wrapper => wrapper.Mode.HasFlag(LaunchWrapperMode.InputLease))
            .Select(wrapper => wrapper.AppId.ToString(CultureInfo.InvariantCulture))
            .ToList();
        if (affected.Count == 0)
        {
            return;
        }

        Log.Warn(
            $"Steam Input Management off - {affected.Count} game(s) still carry the shim-only " +
            $"launch fix (appids: {string.Join(", ", affected)}); re-apply the launch fix to " +
            "switch them to injection.");
    }
}
