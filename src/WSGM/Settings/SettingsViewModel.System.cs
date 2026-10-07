using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Settings;

public sealed partial class SettingsViewModel
{
    /// <summary>Gets the command that reveals wsgm.log in Explorer.</summary>
    public RelayCommand OpenLogLocationCommand { get; }

    /// <summary>
    ///     Gets the command that re-checks Windows' Steam startup entries and turns off any
    ///     that came back. This configures how WSGM starts Steam, which is WSGM's own behavior; the
    ///     exception for touching an external setting is recorded in <c>docs\decisions.md</c>.
    /// </summary>
    public AsyncRelayCommand TakeOverSteamAutostartCommand { get; }

    /// <summary>
    ///     Gets the command that looks for Handheld Companion and the maker's own handheld app again and
    ///     turns off what came back, the way Full mode did in setup. Same recorded exception as the Steam
    ///     autostart takeover: being the one manager of the device is WSGM's own behavior.
    /// </summary>
    public AsyncRelayCommand TakeOverOtherManagersCommand { get; }

    /// <summary>
    ///     Gets the compact logon-service state for the status strip,
    ///     derived from the same flag the boot manifest is projected from.
    /// </summary>
    public string ServiceStateText => StartAtSignIn
        ? $"Sign-in start: {(StartModeIndex == (int)SessionStartMode.Desktop ? "Desktop" : "Game")}"
        : "Sign-in start: off";

    /// <summary>Gets what Windows would still start Steam from, refreshed on demand.</summary>
    public string SteamAutostartStatusText
    {
        get;
        private set => SetField(ref field, value, nameof(SteamAutostartStatusText));
    }

    /// <summary>Gets which other handheld managers WSGM turned off, refreshed on demand.</summary>
    public string OtherManagersStatusText
    {
        get;
        private set => SetField(ref field, value, nameof(OtherManagersStatusText));
    }

    // --- Sign-in behavior ---

    /// <summary>
    ///     Gets or sets whether the logon service starts WSGM at sign-in.
    ///     Persisted via Save; the boot manifest is rewritten there.
    /// </summary>
    public bool StartAtSignIn
    {
        get;
        set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(StartAtSignIn)))
            {
                return;
            }

            Raise(nameof(ServiceStateText));
            Raise(nameof(ShellStatusText));
        }
    }

    /// <summary>
    ///     Gets or sets whether WSGM may own how Steam starts, turning Windows' own Steam
    ///     startup entries off. Persisted via Save; the takeover itself runs after the save, outside
    ///     the config lock, because it may need an elevation prompt.
    /// </summary>
    public bool SteamAutostartTakeoverAccepted
    {
        get;
        set => SetField(ref field, value, nameof(SteamAutostartTakeoverAccepted));
    }

    /// <summary>
    ///     Gets or sets whether WSGM may turn off Handheld Companion and the maker's own handheld app.
    ///     Persisted via Save; the takeover itself runs after the save, outside the config lock, because
    ///     services need an elevation prompt.
    /// </summary>
    public bool OtherManagersTakeoverAccepted
    {
        get;
        set => SetField(ref field, value, nameof(OtherManagersTakeoverAccepted));
    }

    /// <summary>
    ///     Gets or sets the session mode a start produces, as the selector's index:
    ///     0 = Desktop, 1 = Game. Independent of <see cref="StartAtSignIn" />.
    /// </summary>
    public int StartModeIndex
    {
        get;
        set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(StartModeIndex)))
            {
                return;
            }

            Raise(nameof(ServiceStateText));
            Raise(nameof(ShellStatusText));
        }
    }

    /// <summary>Gets or sets muting system audio while the screen is off.</summary>
    public bool MuteWhileDisplayOff
    {
        get;
        set => SetField(ref field, value, nameof(MuteWhileDisplayOff));
    }

    /// <summary>Gets or sets suspending again after a standby wake nothing accounts for.</summary>
    public bool ResuspendUnexplainedWakes
    {
        get;
        set => SetField(ref field, value, nameof(ResuspendUnexplainedWakes));
    }

    /// <summary>Gets Windows' own account of the last standby, for the settings surface.</summary>
    /// <remarks>
    ///     Read once when the page loads rather than polled: it describes the last resume, and nothing
    ///     about it changes while the settings window is open. Windows exposes no documented call for
    ///     what woke the machine, so this never names a cause.
    /// </remarks>
    public string ModernStandbyStatusText
    {
        get;
        private set => SetField(ref field, value, nameof(ModernStandbyStatusText));
    } = "";

    /// <summary>Gets or sets whether the log records debug detail.</summary>
    public bool VerboseLogging
    {
        get;
        set => SetField(ref field, value, nameof(VerboseLogging));
    }

    /// <summary>Gets or sets whether AutoTDP writes a CSV trace of its decisions.</summary>
    public bool AutoTdpTrace
    {
        get;
        set => SetField(ref field, value, nameof(AutoTdpTrace));
    }

    /// <summary>Gets a user-facing explanation of the current sign-in behavior.</summary>
    public string ShellStatusText => !StartAtSignIn
        ? "WSGM does not start at sign-in. Start it from the Start Menu; Explorer stays your Windows shell."
        : StartModeIndex == (int)SessionStartMode.Desktop
            ? "WSGM starts at sign-in and stays in desktop mode until you enter game mode. Explorer stays your Windows shell."
            : "Game mode starts at sign-in through the WSGM logon service. Explorer stays your Windows shell.";

    /// <summary>
    ///     Reads startup sources on a worker. The synchronous Windows adapter waits for an
    ///     asynchronous console command and must never run under the UI synchronization context.
    /// </summary>
    /// <returns>Detected startup entries; the scan itself does not disable them or accept takeover policy.</returns>
    internal async Task<IReadOnlyList<SteamAutostartSource>> ScanSteamAutostartAsync()
    {
        try
        {
            return await Task.Run(_services.ScanSteamAutostart);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Report("Steam autostart scan failed", ex);
            throw;
        }
    }

    /// <summary>Re-scans and, with the takeover accepted, disables what came back.</summary>
    private async Task TakeOverSteamAutostartAsync()
    {
        try
        {
            SteamAutostartStatusText = "Checking how Windows starts Steam…";
            var enabled = (await ScanSteamAutostartAsync()).Where(source => source.Enabled).ToArray();
            if (enabled.Length == 0)
            {
                SteamAutostartStatusText = "WSGM starts Steam; Windows has no Steam startup entry of its own.";
                return;
            }

            // The saved policy decides, not the switch on screen: a second press before saving must
            // still wait for the save it asked for, rather than acting on a choice nothing recorded.
            if (!(await Task.Run(_services.LoadPersisted)).SteamAutostartTakeoverAccepted)
            {
                SteamAutostartStatusText = $"Windows starts Steam from {enabled.Length} place(s). "
                                           + "Turn this on and save to let WSGM own that start.";
                SteamAutostartTakeoverAccepted = true;
                return;
            }

            var result = await Task.Run(() => _services.ApplySteamAutostart(enabled));
            SteamAutostartStatusText = result.Complete
                ? $"Turned off {result.Disabled.Count} Steam startup entry/entries; WSGM starts Steam."
                : "Some Steam startup entries are still enabled: "
                  + string.Join(", ", result.Pending.Concat(result.NeedsElevation).Select(source => source.Describe()));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Report("Steam autostart takeover failed", ex);
            SteamAutostartStatusText = $"Could not read Windows' startup entries: {ex.Message}";
        }
    }

    /// <summary>Looks for the other managers again and, with the takeover accepted, turns off what came back.</summary>
    private async Task TakeOverOtherManagersAsync()
    {
        try
        {
            OtherManagersStatusText = "Looking for other handheld managers…";
            // On a worker: detection reads the task scheduler through schtasks.
            var detected = await Task.Run(_services.DetectOtherManagers);
            if (detected.Count == 0)
            {
                OtherManagersStatusText = "Nothing else manages this device. "
                                          + DescribeOtherManagers(await Task.Run(_services.LoadPersisted));
                return;
            }

            var found = string.Join("; ", detected.Select(manager => manager.Describe()));

            // The saved policy decides, not the switch on screen: a second press before saving must
            // still wait for the save it promised, rather than turning services off with nothing
            // recorded to restore them from.
            if (!(await Task.Run(_services.LoadPersisted)).OtherManagersTakeoverAccepted)
            {
                OtherManagersStatusText = $"Found {found}. Save to let WSGM turn them off.";
                OtherManagersTakeoverAccepted = true;
                return;
            }

            var result = await Task.Run(() => _services.ApplyOtherManagers(detected));
            OtherManagersStatusText = result.Failed.Count == 0
                ? $"Turned off {result.Disabled.Count} service(s) and task(s)."
                  + (result.StillRunning.Count == 0
                      ? ""
                      : $" Still running until you close them or sign out: {string.Join(", ", result.StillRunning)}.")
                : "Could not turn off " + string.Join(", ", result.Failed) + ".";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Report("Other managers takeover failed", ex);
            OtherManagersStatusText = $"Could not look for other managers: {ex.Message}";
        }
    }

    // Described from what was recorded, never by detecting here: like the Steam autostart line, the task
    // scheduler is too slow for the path that opens this window.
    private static string DescribeOtherManagers(AppConfig config)
    {
        if (!config.OtherManagersTakeoverAccepted)
        {
            return "Handheld Companion or the maker's own app (Armoury Crate, MSI Center M, Legion Space) may still "
                   + "run beside WSGM and answer the device's buttons. Check and take over.";
        }

        var recorded = OtherManagers.DescribeRecords(config.OtherManagersDisabled);
        return recorded.Length == 0
            ? "WSGM is the only manager of this device. Nothing else had to be turned off."
            : recorded + " and are restored when WSGM is uninstalled.";
    }

    private void OpenLogLocation()
    {
        try
        {
            var log = Path.Combine(Store.Context.Root, "wsgm.log");
            // Game mode has no Explorer in the session, and WSGM is normally elevated:
            // starting explorer.exe here would either break UWP for the session (an
            // elevated Explorer; see docs\elevation.md) or bring its taskbar up next to WSGM's
            // own tray host. Show the path instead; the user can open it in desktop mode.
            if (!ExplorerControl.IsDesktopShellRunning())
            {
                Log.Info(
                    $"Open log location: no Explorer in this session — showing the path instead ({Store.Context.Root}).");
                StatusText = $"Log folder: {Store.Context.Root} (open it in desktop mode)";
                return;
            }

            // Absolute system path: a relative name would resolve via the process
            // working directory, which is the user-writable install dir.
            var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var explorer = Path.Combine(windir, "explorer.exe");
            // Select the file when it exists so the user lands right on it;
            // otherwise just open the folder.
            var psi = File.Exists(log)
                ? new ProcessStartInfo(explorer, $"/select,\"{log}\"")
                : new ProcessStartInfo(Store.Context.Root);
            psi.UseShellExecute = true;
            psi.WorkingDirectory = windir;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open the log location: {ex.Message}");
            StatusText = $"Could not open the log location: {ex.Message}";
        }
    }
}
