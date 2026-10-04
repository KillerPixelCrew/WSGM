using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Install;
using WSGM.Shell;

namespace WSGM;

/// <summary>The intentionally narrow operating modes accepted by the executable.</summary>
public enum RunMode
{
    /// <summary>
    ///     Runs the game-mode shell session (service boot or --shell). Explorer
    ///     stays the registered Windows shell; this session ends it and takes the screen.
    /// </summary>
    Shell,

    /// <summary>Runs the settings or welcome UI without changing shell state.</summary>
    Settings,

    /// <summary>Runs the manual overlay smoke-test session.</summary>
    OverlayTest
}

/// <summary>Defines the safe command-line entry points and application bootstrap.</summary>
public static class Program
{
    /// <summary>Exit code of <c>--uninstall-restore</c> when HidHide cleanup was not verified.</summary>
    internal const int UninstallHidHideUnverifiedExitCode = 3;

    private static Mutex? _shellMutex;
    private static ConfigStore Store { get; set; } = null!;

    /// <summary>Gets the mode selected from the current command line.</summary>
    public static RunMode Mode { get; private set; } = RunMode.Settings;

    /// <summary>
    ///     Gets whether this shell process was launched by the logon service
    ///     (--boot): the session boots over a live, still-initializing explorer that
    ///     the takeover flow waits out and then cleanly shuts down.
    /// </summary>
    public static bool ServiceBoot { get; private set; }

    /// <summary>Gets whether startup must remain resident on Desktop, even before Explorer appears.</summary>
    public static bool DesktopResident { get; private set; }

    /// <summary>Starts the selected supported application mode.</summary>
    /// <param name="args">The command-line arguments passed to the executable.</param>
    /// <returns>The process exit code.</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        // Roslyn implements an async Main through a separate synchronous entry point and does not
        // carry STAThread onto that synthesized method. Keep the real process entry point
        // synchronous so normal startup and Avalonia begin on the Windows STA thread; command-only
        // maintenance may still yield inside the private body while this wrapper owns its result.
        return MainAsync(args).GetAwaiter().GetResult();
    }

    private static async Task<int> MainAsync(string[] args)
    {
        HashSet<string> flags = new(args, StringComparer.OrdinalIgnoreCase);
        // Hidden fixed-purpose process used only to preserve normal Explorer parent/job
        // semantics across a shell transition. It must run before logging, package discovery,
        // elevation, Avalonia, or any other WSGM service. The mode accepts no executable or
        // argument input and can start only the canonical Windows Explorer path.
        if (ExplorerShellAnchor.TryRunProcessMode(args, out var anchorExitCode))
        {
            return anchorExitCode;
        }

        if (flags.Contains("--desktop-shell-probe"))
        {
            return ExplorerControl.IsDesktopShellRunning() ? 0 : 1;
        }

        Store = new ConfigStore(UserDataContext.ForCurrentUser());

        // Recovery path: must work even when Avalonia/GPU/config are broken.
        // Keep this ahead of logging too: a broken profile directory must never
        // prevent the user from getting their desktop back.
        if (flags.Contains("--restore-shell"))
        {
            return await RestoreShellAsync().ConfigureAwait(false);
        }

        // Quiet shell-registration restore for the setup's uninstall: no explorer
        // start, no UI — the uninstaller drives everything else.
        if (flags.Contains("--unregister-shell"))
        {
            ShellRegistration.Uninstall(Store);
            SteamInputBlocker.ReleaseBestEffort("unregister-shell");
            return 0;
        }

        Log.Init("wsgm", Store.Context.Root);
        var startupConfig = Store.Load();
        // Before anything else reads configuration, so a startup problem is captured at the
        // verbosity the device is actually set to. The flag wins over the stored choice for this
        // run, which is how a one-off reproduction is captured without persisting a setting.
        ApplyLogVerbosity(args, startupConfig);
        // The Steam UI machinery writes through its own sink so it carries no dependency on this
        // application's logger. Installed here, right after Log.Init, because remote diagnosis of
        // the CEF surface is a pasted wsgm.log and a missed install would silently empty it.
        WsgmSteamUiLog.Install();

        if (RunOneShot(flags) is { } oneShotExitCode)
        {
            return oneShotExitCode;
        }

        // Elevated one-shot for the uninstaller. The controller comes first: shows every device WSGM
        // hid with HidHide again, before HidHide may be removed and before user data may be deleted,
        // then puts back every machine-level setting WSGM changed (display scaling, UAC,
        // lock-on-wake). Exit code 3 tells setup the HidHide cleanup could not be verified; the
        // ownership ledger is then kept for another attempt.
        if (flags.Contains("--uninstall-restore"))
        {
            var hidHide = await RestoreHidHideForUninstallAsync().ConfigureAwait(false);
            Installer.RestoreMachineSettings(Store);
            return hidHide ? 0 : UninstallHidHideUnverifiedExitCode;
        }

        if (ArgumentValue(args, "--export-setup-answers=") is { } exportPath)
        {
            return ExportSetupAnswers(exportPath);
        }

        if (flags.Contains("--setup"))
        {
            return RunSetup(args);
        }

        ServiceBoot = IsServiceBoot(args);
        DesktopResident = flags.Contains("--desktop-resident");
        Mode = DecideMode(args);
        if (Mode == RunMode.Settings && SettingsActivation.TryRequest())
        {
            Log.Info("Settings launch handed to the resident WSGM input owner.");
            return 0;
        }

        if (ServiceBoot)
        {
            Log.Info($"Run mode: {Mode} (service boot, elevated={ElevationCheck.IsCurrentProcessElevated()}, " +
                     $"session {WindowFinder.CurrentSessionId})");
        }
        else
        {
            Log.Info($"Run mode: {Mode}");
        }

        if (Mode == RunMode.Shell)
        {
            if (InstallLayout.HasPendingSetup)
            {
                Log.Error(
                    "WSGM setup has an incomplete file transaction. Run setup to repair it before starting the session.");
                return 1;
            }

            // Shell only — --overlay-test is a dev-machine surface and must not
            // trigger a UAC prompt or relaunch elevated.
            // Must run before the shell mutex: the elevated copy takes the mutex,
            // this process only lingers as Winlogon's watched shell process.
            var handedOver = SelfElevation.EnsureElevatedIfConfigured(args, startupConfig);
            if (handedOver is not null)
            {
                return handedOver.Value;
            }
        }

        using var activation = Mode == RunMode.Shell
            ? new EventWaitHandle(false, EventResetMode.AutoReset, SessionActivation.EventName)
            : null;
        if (Mode == RunMode.Shell)
        {
            if (flags.Contains("--activate"))
            {
                activation!.Set();
            }

            if (!AcquireShellMutex())
            {
                Log.Warn("Another WSGM shell instance is running; exiting.");
                return 0;
            }

            // Record this start BEFORE deciding, so the breaker fires on the
            // 3rd start within 2 minutes (this one included) as documented.
            CrashLoopBreaker.RecordStart(Store.Context.Root);
            if (CrashLoopBreaker.IsLooping(Store.Context.Root))
            {
                return DisarmCrashLoop(startupConfig);
            }
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Panic("UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        UpdateExitWatcher.Start(
            () => RequestInstallerExit(ApplicationShutdownReason.Update),
            () => RequestInstallerExit(ApplicationShutdownReason.Uninstall),
            Mode == RunMode.Shell ? RequestRestoreShellExit : null);

        try
        {
            var exitCode = BuildAvaloniaApp(startupConfig, Store).StartWithClassicDesktopLifetime(args);
            // Normal shutdown. Settings-only processes skip release unless they
            // acquired a lease themselves (overlay test).
            if (Mode is RunMode.Shell or RunMode.OverlayTest || SteamInputBlocker.IsApplied)
            {
                SteamInputBlocker.ReleaseBestEffort("shutdown");
            }

            if (Mode != RunMode.Shell)
            {
                return exitCode;
            }

            RestoreDisplayScalesBestEffort();
            // A clean exit is NOT a crash: without this, two update restarts
            // plus a sign-in inside 2 minutes read as a crash loop and disarm
            // the shell (device-observed). Only dirty deaths — which never
            // reach this line — may accumulate toward the breaker.
            CrashLoopBreaker.Reset(Store.Context.Root);
            return exitCode;
        }
        catch (Exception ex)
        {
            Panic("Avalonia lifetime crashed", ex);
            return 1;
        }
    }

    /// <summary>Gives the user the desktop back; must work when config, logging and Avalonia cannot.</summary>
    /// <returns>Process exit code.</returns>
    private static async Task<int> RestoreShellAsync()
    {
        ShellRegistration.Uninstall(Store);
        // The user is escaping game mode: also disarm the sign-in start so the
        // next sign-in is a plain desktop (re-enable in Settings). Best effort —
        // this path must survive a broken profile, and logging is not up yet.
        // boot.json is projected from a defensive load so the disarm still lands
        // when config.json cannot be read; clearing the flag INSIDE config.json is
        // a read-modify-write and goes through the strict mutation path, which
        // aborts rather than replacing the registry recovery snapshots with
        // defaults.
        AppConfig? recoveryConfig = null;
        try
        {
            recoveryConfig = Store.Load();
            BootManifestWriter.WriteSignInDisabled(recoveryConfig, Store.Context);
        }
        catch (Exception)
        {
            /* Best effort: logging is not up yet. */
        }

        try
        {
            recoveryConfig = Store.Mutate(static c => c.StartAtSignIn = false);
        }
        catch (Exception)
        {
            /* Best effort: logging is not up yet. */
        }

        // A resident WSGM shell still owns its Shell_TrayWnd and would keep running with its
        // registration and sign-in start changed underneath it. Ask it to shut down normally,
        // which restores Explorer itself; the start below then finds the desktop running.
        UpdateExitWatcher.RequestResidentShellExit(TimeSpan.FromSeconds(45));
        // Verify-and-wait: this path returns out of Main straight afterwards, so a
        // queued de-elevation check would be torn down before it ran and the user
        // would be left with an elevated Explorer (breaks UWP); see docs\elevation.md.
        ExplorerControl.StartExplorerAndVerify();
        try
        {
            using var recoveryBudget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await GameModeReturnRecovery.RestorePendingAsync(Store, recoveryBudget.Token, report: static _ => { })
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Explorer recovery remains usable when optional saved state cannot be restored.
        }

        // A lease is pipe-backed, so a crashed shell releases it when Windows
        // closes its handles. A live shell can still be releasing normally.
        SteamInputBlocker.ReleaseBestEffort("restore-shell");
        RestoreDisplayScalesBestEffort(recoveryConfig);
        return 0;
    }

    /// <summary>Runs the first fixed-purpose one-shot named on the command line.</summary>
    /// <param name="flags">The command-line flags.</param>
    /// <returns>The one-shot's exit code, or null when the command line names none.</returns>
    /// <remarks>
    ///     The table order is the precedence. Each entry is a command an elevated relaunch, setup or
    ///     the uninstaller runs, and none takes input beyond its flag.
    /// </remarks>
    private static int? RunOneShot(HashSet<string> flags)
    {
        (string Flag, Func<int> Run)[] oneShots =
        [
            // Elevated one-shots for the UAC prompt-level toggle (see UacSettings).
            ("--set-uac-silent", static () => UacSettings.ApplyDirect(Store, true) ? 0 : 1),
            ("--restore-uac", static () => UacSettings.ApplyDirect(Store, false) ? 0 : 1),
            // Elevated one-shots for the Steam autostart takeover (see SteamAutostartService). Neither
            // takes a source name from the command line: the elevated instance rescans and decides.
            (SteamAutostartService.DisableArgument, () => SteamAutostartService.RunElevatedDisable(Store)),
            (SteamAutostartService.RestoreArgument, () => SteamAutostartService.RestoreAll(Store)),
            // Elevated one-shot for the other-managers takeover (see OtherManagers): Settings > System
            // runs it when WSGM is not elevated, and the elevated instance detects for itself.
            (OtherManagers.DisableArgument, () => OtherManagers.RunElevatedDisable(Store)),
            ("--disable-lock-on-wake", static () => LockScreenSettings.ApplyDirect(Store, true) ? 0 : 1),
            ("--restore-lock-on-wake", static () => LockScreenSettings.ApplyDirect(Store, false) ? 0 : 1),
            // Elevated one-shots for the Steam Input shim. Steam normally lives under
            // Program Files, which a desktop-mode Settings process cannot write, so the
            // Settings save path re-runs itself through these when a write is refused.
            ("--apply-steam-input-shim", ApplySteamInputShim),
            ("--remove-steam-input-shim", RemoveSteamInputShim),
            ("--restore-steam-chord-template", RestoreSteamChordTemplate)
        ];
        foreach (var (flag, run) in oneShots)
        {
            if (flags.Contains(flag))
            {
                return run();
            }
        }

        return null;
    }

    private static int ApplySteamInputShim()
    {
        SteamInputShim.SetEnabled(true);
        return SteamInputShim.Reconcile("elevated-apply").State
            is SteamInputShimState.Deployed or SteamInputShimState.UpdatePending
            ? 0
            : 1;
    }

    private static int RemoveSteamInputShim()
    {
        SteamInputShim.Remove("uninstall");
        return 0;
    }

    private static int RestoreSteamChordTemplate()
    {
        // Uninstall: Valve's guide chord template back in place, the backup gone. Nothing to do
        // without Steam or without a backup, and both are success.
        if (Steam.InstallDirectory is { } steamDirectory)
        {
            SteamGuideChordMirror.RestoreInstalledSteam(steamDirectory);
        }

        return 0;
    }

    /// <summary>Applies setup's answers and the install-time guards once setup has placed the payload.</summary>
    /// <param name="args">Process arguments, which may name the answers file.</param>
    /// <returns>Process exit code.</returns>
    private static int RunSetup(string[] args)
    {
        // The gaming-home guard captures a registry snapshot INTO this config and
        // saves it, so it is loaded strictly: an unreadable config.json aborts the
        // capture instead of recording the already-modified value as the pre-WSGM
        // one and persisting defaults over every other recovery snapshot. Setup
        // Without answers, the recovery one-shot can still complete. With answers, the profile step
        // must report failure when no configuration could be loaded.
        AppConfig? config = null;
        // The existing file distinguishes a first install from a repair or upgrade; only the first
        // configuration may seed controller management from the installation choice.
        var freshInstall = !File.Exists(Store.ConfigPath);
        try
        {
            config = Store.LoadForMutation();
        }
        catch (Exception ex)
        {
            Log.Error("Setup: config.json is unreadable — skipping the gaming-home guard and the boot manifest",
                ex);
        }

        if (config is not null && ArgumentValue(args, "--answers=") is { } answersPath)
        {
            try
            {
                var answers = SetupAnswers.Parse(File.ReadAllBytes(answersPath));
                var steamTakeover = false;
                var managersTakeover = false;
                config = Store.Mutate(fresh =>
                {
                    steamTakeover = answers.SteamAutostartTakeover && !fresh.SteamAutostartTakeoverAccepted;
                    managersTakeover = answers.OtherManagersTakeover && !fresh.OtherManagersTakeoverAccepted;
                    answers.ApplyTo(fresh, freshInstall);
                });
                Log.Info($"Setup: applied the setup answers to a {(freshInstall ? "fresh" : "existing")} "
                         + $"configuration ({answers.Describe()}, controllerManagement={config.DeviceIntegration.ControllerManagementEnabled}).");
                if (steamTakeover)
                {
                    // The user consented in setup; setup never sets this for a silent fresh install.
                    var result = SteamAutostartService.Apply(Store, 
                        [.. SteamAutostartService.Scan().Where(source => source.Enabled)], false);
                    Log.Info($"Setup: Steam autostart takeover disabled {result.Disabled.Count}, "
                             + $"pending {result.Pending.Count}, needing elevation {result.NeedsElevation.Count}.");
                }

                if (managersTakeover)
                {
                    // Chosen in setup (Full mode, or Customize). Each change is recorded before it is made,
                    // and --uninstall-restore puts it back.
                    var result = OtherManagers.Disable(OtherManagers.Detect(), entry => OtherManagers.Record(Store, entry));
                    if (result.Failed.Count > 0 || result.StillRunning.Count > 0)
                    {
                        Log.Warn($"Setup: other managers: failed [{string.Join(", ", result.Failed)}], still "
                                 + $"running [{string.Join(", ", result.StillRunning)}].");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log.Error("Setup: the setup answers could not be applied", ex);
                return 1;
            }
        }

        Log.Info($"Setup: installed at {Installer.InstalledExePath}.");
        // Deploy the Steam Input shim only after the payload exists in the
        // install directory. Default-on when config.json is unreadable, because
        // on is the default the property itself carries.
        SteamInputShim.SetEnabled(config?.SteamInputManagementEnabled ?? true);
        SteamInputShim.Reconcile("setup");
        // Self-guarding no-op unless a snapshotted shell value needs restoring —
        // WSGM boots via the logon service over an explorer shell.
        ShellRegistration.Uninstall(Store);
        if (config is null)
        {
            return ArgumentValue(args, "--answers=") is null ? 0 : 1;
        }

        ShellRegistration.ApplyGamingHomeGuard(Store, config);
        return BootManifestWriter.WriteCurrent(config, Store.Context) ? 0 : 1;
    }

    /// <summary>Disarms the sign-in start after a crash loop and hands the session back to Explorer.</summary>
    /// <param name="startupConfig">The configuration loaded for this process startup.</param>
    /// <returns>Process exit code, always a failure.</returns>
    private static int DisarmCrashLoop(AppConfig startupConfig)
    {
        var recoveryConfig = startupConfig;
        Log.Error("Crash loop detected (3+ shell starts within 2 minutes) — " +
                  "the sign-in start is DISABLED (re-enable in WSGM settings).");
        // Disarm the sign-in start: the manifest write works even when
        // config.json cannot be saved, so the next sign-in stays a desktop.
        try
        {
            BootManifestWriter.WriteSignInDisabled(recoveryConfig, Store.Context);
        }
        catch (Exception ex)
        {
            Log.Warn($"Crash-loop disarm: boot manifest write failed: {ex.Message}");
        }

        try
        {
            // Read-modify-write, so the strict mutation load: an unreadable
            // config.json aborts here instead of overwriting the registry
            // recovery snapshots with defaults. boot.json above already
            // disarmed the next sign-in either way.
            recoveryConfig = Store.Mutate(static c => c.StartAtSignIn = false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Crash-loop disarm: could not clear the sign-in start flag: {ex.Message}");
        }

        ShellRegistration.Uninstall(Store);
        if (!ExplorerControl.IsDesktopShellRunning())
        {
            // Same reason as --restore-shell: the disarm exits immediately after
            // this, so the elevation repair has to complete before we return.
            ExplorerControl.StartExplorerAndVerify();
        }

        // Lease release first (invariant: fires on EVERY recovery path,
        // ahead of cosmetic restores) — same ordering as --restore-shell.
        SteamInputBlocker.ReleaseBestEffort("crash-loop");
        RestoreDisplayScalesBestEffort(recoveryConfig);
        // Clear the marker so the next manual start isn't instantly disarmed.
        CrashLoopBreaker.Reset(Store.Context.Root);
        return 1;
    }

    // A --restore-shell run from another process: the normal shutdown path restores Explorer and
    // retires this shell's taskbar before that process touches the desktop.
    private static void RequestRestoreShellExit()
    {
        Dispatcher.UIThread.Post(() =>
        {
            ApplicationShutdownRequest.Request(ApplicationShutdownReason.Normal);
            _ = ((App)Application.Current!).Runtime.RequestExit();
        });
    }

    private static void RequestInstallerExit(ApplicationShutdownReason reason)
    {
        // Posted jobs only run once StartWithClassicDesktopLifetime pumps the dispatcher.
        Dispatcher.UIThread.Post(() => RunInstallerExitRequest(
            reason,
            Steam.StopForUpdate,
            ApplicationShutdownRequest.Request,
            () => { _ = ((App)Application.Current!).Runtime.RequestExit(); }));
    }

    /// <summary>
    ///     The installer-exit ordering, separated from the dispatcher and from Steam so it
    ///     can be proven without either.
    /// </summary>
    /// <remarks>
    ///     Update reserves one bounded Steam/wrapper pre-stop window before the application's own
    ///     cleanup deadline; the installer waits for both windows plus handoff margin before its force
    ///     fallback. The try/finally is the contract: a failed pre-stop can never prevent WSGM cleanup
    ///     from starting. Uninstall deliberately does not stop Steam.
    /// </remarks>
    internal static void RunInstallerExitRequest(
        ApplicationShutdownReason reason,
        Action stopForUpdate,
        Action<ApplicationShutdownReason> requestShutdown,
        Action shutdownLifetime)
    {
        try
        {
            if (reason is ApplicationShutdownReason.Update)
            {
                stopForUpdate();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Steam/update-helper pre-stop failed; WSGM cleanup will still run", ex);
        }
        finally
        {
            requestShutdown(reason);
            shutdownLifetime();
        }
    }

    /// <summary>
    ///     Resolves the requested mode from explicit flags. No flag means the
    ///     safe Settings surface; shell mode is only ever explicit (--shell/--boot).
    /// </summary>
    internal static RunMode DecideMode(string[] args)
    {
        if (args.Contains("--shell", StringComparer.OrdinalIgnoreCase) || IsServiceBoot(args))
        {
            return RunMode.Shell;
        }

        if (args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
        {
            return RunMode.Settings;
        }

        return args.Contains("--overlay-test", StringComparer.OrdinalIgnoreCase)
            ? RunMode.OverlayTest
            : RunMode.Settings;
    }

    /// <summary>Returns the value of a <c>--name=value</c> argument, or null when it is absent.</summary>
    /// <param name="args">Process arguments.</param>
    /// <param name="prefix">The argument name including its <c>=</c>.</param>
    internal static string? ArgumentValue(string[] args, string prefix)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args
            .Where(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(argument => argument[prefix.Length..])
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    /// <summary>Writes the current configuration as setup answers, for setup's profile page.</summary>
    /// <param name="path">Where to write the answers.</param>
    /// <returns>Process exit code.</returns>
    private static int ExportSetupAnswers(string path)
    {
        try
        {
            var freshInstall = !File.Exists(Store.ConfigPath);
            // Export runs before the user confirms setup. Read without saving or quarantining the file.
            var config = freshInstall ? new AppConfig() : Store.LoadForMutation();
            IReadOnlyList<string> entries = [];
            try
            {
                entries =
                [
                    .. SteamAutostartService.Scan().Where(source => source.Enabled).Select(source => source.Describe())
                ];
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn("Setup answers: the Steam autostart scan failed: " + ex.Message);
            }

            IReadOnlyList<string> managers = [];
            try
            {
                managers = [.. OtherManagers.Detect().Select(manager => manager.Describe())];
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn("Setup answers: the other-manager scan failed: " + ex.Message);
            }

            File.WriteAllBytes(path, SetupAnswers.Export(config, freshInstall, entries, managers).ToUtf8Json());
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error("Setup answers could not be exported", ex);
            return 1;
        }
    }

    /// <summary>Shows every device WSGM hid again and takes WSGM off HidHide's allowlist.</summary>
    /// <returns>Whether HidHide took every change.</returns>
    private static async Task<bool> RestoreHidHideForUninstallAsync()
    {
        try
        {
            HidHideOwnership hidHide = new(
                new NativeHidHideControl(),
                new FileHidHideOwnershipStore(Path.Combine(Store.Context.Root, "hidhide-ownership.json")));
            var result = await hidHide.ShowForUninstallAsync(
                [Environment.ProcessPath ?? Installer.InstalledExePath],
                CancellationToken.None).ConfigureAwait(false);
            if (result.Succeeded)
            {
                Log.Info("Uninstall restore: HidHide: " + result.Detail);
            }
            else
            {
                Log.Warn("Uninstall restore: HidHide cleanup incomplete: " + result.Detail);
            }

            return result.Succeeded;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Uninstall restore: HidHide cleanup failed", ex);
            return false;
        }
    }

    /// <summary>
    ///     True when the command line carries the logon service's --boot flag
    ///     (kept pure so mode precedence stays testable without a live session).
    /// </summary>
    internal static bool IsServiceBoot(string[] args)
    {
        return args.Contains("--boot", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Reports whether this run was asked for verbose logging on the command line.</summary>
    /// <param name="args">Process arguments.</param>
    /// <returns>True when <c>--verbose</c> is present.</returns>
    internal static bool HasVerboseFlag(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Resolves this run's log verbosity from the command line, else configuration.</summary>
    /// <param name="args">Process arguments.</param>
    /// <param name="config">The configuration loaded for this process startup.</param>
    /// <param name="store">The process-owned configuration persistence.</param>
    /// <remarks>
    ///     Configuration is read defensively: a damaged config.json must not decide whether the log
    ///     that would explain the damage exists. Any failure keeps the default.
    /// </remarks>
    private static void ApplyLogVerbosity(string[] args, AppConfig config)
    {
        var verbosity = LogVerbosity.Normal;
        if (HasVerboseFlag(args))
        {
            verbosity = LogVerbosity.Verbose;
        }
        else
        {
            try
            {
                verbosity = config.LogVerbosity;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or InvalidDataException or JsonException)
            {
                Log.Warn($"Log verbosity fell back to {verbosity}: {ex.Message}");
            }
        }

        Log.SetVerbosity(verbosity);
    }

    private static bool AcquireShellMutex()
    {
        _shellMutex = new Mutex(true, WSGM.Shared.SessionProtocolNames.ShellMutex, out var createdNew);
        if (createdNew)
        {
            return true;
        }

        // The named object survives while ANY handle to it is open (installer
        // probe, diagnostic tool), so createdNew=false only proves it existed —
        // try to actually take ownership before concluding a shell is running.
        try
        {
            return _shellMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // Previous owner died without releasing; ownership passed to us.
            return true;
        }
    }

    /// <summary>
    ///     Fatal-error handler for shell mode: make sure the session has a
    ///     desktop again, then die. The logon service watchdog is the robust outer
    ///     recovery layer; this is in-process best effort.
    /// </summary>
    private static void Panic(string context, Exception? ex)
    {
        Log.Error($"PANIC ({context})", ex ?? new Exception("unknown"));
        if (Mode == RunMode.Shell)
        {
            try
            {
                var ownershipPath = Path.Combine(Store.Context.Root, "hidhide-ownership.json");
                if (File.Exists(ownershipPath))
                {
                    var budget = ApplicationShutdownCoordinator.BudgetFor(ApplicationShutdownReason.SessionEnd);
                    using CancellationTokenSource cleanup = new(budget);
                    Task.Run(() => new HidHideOwnership(new NativeHidHideControl(),
                            new FileHidHideOwnershipStore(ownershipPath)).ShowAsync(cleanup.Token))
                        .Wait(budget);
                }
            }
            catch
            {
                // The dispatcher has stopped. Native cloak recovery must never prevent shell recovery.
            }

            ShellRegistration.Uninstall(Store);
            // Best-effort (fails from a non-UI thread, and the dying process
            // destroys the window anyway): don't leave our Shell_TrayWnd up while
            // explorer's taskbar comes back.
            try
            {
                TrayHost.DestroyActive();
            }
            catch
            {
                /* recovery must not throw */
            }

            if (!ExplorerControl.IsDesktopShellRunning())
            {
                if (ExplorerShellAnchor.HasRecoveryOwner(WindowFinder.CurrentSessionId))
                {
                    // The verified medium/jobless anchor restores Explorer after this process
                    // actually exits. Starting one here would race that exact owner and the
                    // service watchdog, and could recreate the job-bound desktop Q02 removes.
                    Log.Info("Panic recovery delegated to the verified Explorer shell anchor.");
                }
                else
                {
                    ExplorerControl.StartExplorer();
                }
            }

            RestoreDisplayScalesBestEffort();
            GameModeReturnRecovery.RestoreBestEffort(Store);
        }

        // Same guard as normal shutdown: a crashing settings process must not
        // release a still-running shell's lease.
        if (Mode is RunMode.Shell or RunMode.OverlayTest || SteamInputBlocker.IsApplied)
        {
            SteamInputBlocker.ReleaseBestEffort("panic");
        }
    }

    /// <summary>
    ///     Game mode forces 100% scaling and that persists in the registry —
    ///     every way out of shell mode must put the captured values back.
    /// </summary>
    private static void RestoreDisplayScalesBestEffort(AppConfig? config = null)
    {
        try
        {
            DisplayScale.RestoreSaved(Store, config ?? Store.Load());
        }
        catch
        {
            // Recovery paths must never be blocked by scaling cleanup.
        }
    }

    /// <summary>Builds the Avalonia application configuration used by all UI modes.</summary>
    /// <param name="config">The configuration loaded for this process startup.</param>
    /// <returns>The configured Avalonia application builder.</returns>
    // ReSharper disable once MemberCanBePrivate.Global
    public static AppBuilder BuildAvaloniaApp(AppConfig config, ConfigStore store)
    {
        ArgumentNullException.ThrowIfNull(config);
        return AppBuilder.Configure(() => new App(config, store))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}

/// <summary>
///     Disarms WSGM if the shell process keeps dying at logon: 3 or more
///     shell-mode starts within 2 minutes disarm the service boot automatically
///     (boot.json disabled plus the config flag cleared), so the next sign-in is a plain
///     Explorer desktop. Dropping a legacy shell registration is a migration remnant of
///     the same disarm, not its primary action.
/// </summary>
internal static class CrashLoopBreaker
{
    private static string MarkerPath(string root) => Path.Combine(root, "shell-starts.txt");

    public static void RecordStart(string root)
    {
        try
        {
            File.AppendAllText(MarkerPath(root), DateTime.UtcNow.ToString("O") + Environment.NewLine);
        }
        catch (Exception)
        {
            // Best effort: a missing marker only weakens crash-loop detection.
        }
    }

    /// <summary>Call AFTER RecordStart so the current start counts toward the 3.</summary>
    public static bool IsLooping(string root)
    {
        try
        {
            if (!File.Exists(MarkerPath(root)))
            {
                return false;
            }

            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(2);
            var all = File.ReadAllLines(MarkerPath(root))
                .Select(l =>
                    DateTime.TryParse(l, null, DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue)
                .Where(t => t != DateTime.MinValue)
                .ToArray();
            var recent = all.Count(t => t > cutoff);
            if (recent >= 3)
            {
                return true;
            }

            // Trim stale entries so the file doesn't grow forever.
            if (recent < all.Length)
            {
                File.WriteAllLines(MarkerPath(root), all.Where(t => t > cutoff).Select(t => t.ToString("O")));
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Clears the marker after the breaker fired, so the next manual
    ///     shell start begins with a clean slate instead of being disarmed again.
    /// </summary>
    public static void Reset(string root)
    {
        try
        {
            File.Delete(MarkerPath(root));
        }
        catch (Exception)
        {
            // Best effort: a stale marker only makes the next loop check stricter.
        }
    }
}
