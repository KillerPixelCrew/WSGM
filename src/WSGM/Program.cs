using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using WSGM.Core;

namespace WSGM;

/// <summary>The intentionally narrow operating modes accepted by the executable.</summary>
public enum RunMode
{
    /// <summary>Runs the registered Windows shell session.</summary>
    Shell,

    /// <summary>Runs the settings or welcome UI without changing shell state.</summary>
    Settings,

    /// <summary>Runs the manual overlay smoke-test session.</summary>
    OverlayTest,
}

/// <summary>Defines the safe command-line entry points and application bootstrap.</summary>
public static class Program
{
    /// <summary>Gets the mode selected from the current command line.</summary>
    public static RunMode Mode { get; private set; } = RunMode.Settings;
    private static Mutex? _shellMutex;

    /// <summary>Starts the selected supported application mode.</summary>
    /// <param name="args">The command-line arguments passed to the executable.</param>
    /// <returns>The process exit code.</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        // Recovery path: must work even when Avalonia/GPU/config are broken.
        // Keep this ahead of logging too: a broken profile directory must never
        // prevent the user from getting their desktop back.
        if (args.Contains("--restore-shell", StringComparer.OrdinalIgnoreCase))
        {
            ShellRegistration.Uninstall();
            ExplorerControl.StartExplorer();
            // A crashed shell may have left the Steam Input layout pinned; this
            // fresh process cannot know, so reset unconditionally (never throws).
            SteamInputPin.ReleaseBestEffort("restore-shell");
            RestoreDisplayScalesBestEffort();
            // Restore exactly what this device had before WSGM, rather than
            // creating a posture signal on a normal PC. Wrapped because this
            // recovery path must never throw.
            try { SlateMode.RestoreOriginal(); } catch { }
            return 0;
        }

        // Quiet shell-registration restore for the Inno uninstaller: no explorer
        // start, no UI — the uninstaller drives everything else.
        if (args.Contains("--unregister-shell", StringComparer.OrdinalIgnoreCase))
        {
            ShellRegistration.Uninstall();
            SteamInputPin.ReleaseBestEffort("unregister-shell");
            return 0;
        }

        Log.Init();

        // Elevated one-shots for the UAC prompt-level toggle (see UacSettings).
        if (args.Contains("--set-uac-silent", StringComparer.OrdinalIgnoreCase))
        {
            return UacSettings.ApplyDirect(disablePrompts: true) ? 0 : 1;
        }
        if (args.Contains("--restore-uac", StringComparer.OrdinalIgnoreCase))
        {
            return UacSettings.ApplyDirect(disablePrompts: false) ? 0 : 1;
        }
        if (args.Contains("--disable-lock-on-wake", StringComparer.OrdinalIgnoreCase))
        {
            return LockScreenSettings.ApplyDirect(disableSignInOnWake: true) ? 0 : 1;
        }
        if (args.Contains("--restore-lock-on-wake", StringComparer.OrdinalIgnoreCase))
        {
            return LockScreenSettings.ApplyDirect(disableSignInOnWake: false) ? 0 : 1;
        }

        // Elevated one-shot for the uninstaller: puts back every machine-level
        // setting WSGM changed (UAC, lock-on-wake, slate posture).
        if (args.Contains("--uninstall-restore", StringComparer.OrdinalIgnoreCase))
        {
            Installer.RestoreMachineSettings();
            return 0;
        }

        if (args.Contains("--uninstall-app", StringComparer.OrdinalIgnoreCase))
        {
            Installer.UninstallApp();
            return 0;
        }

        if (args.Contains("--install", StringComparer.OrdinalIgnoreCase))
        {
            Installer.InstallApp();
            return 0;
        }

        if (args.Contains("--setup", StringComparer.OrdinalIgnoreCase))
        {
            var config = ConfigStore.Load();
            Installer.InstallApp();
            ShellRegistration.Install(config);
            return 0;
        }

        Mode = DecideMode(args);
        Log.Info($"Run mode: {Mode}");

        if (Mode == RunMode.Shell)
        {
            // Shell only — --overlay-test is a dev-machine surface and must not
            // trigger a UAC prompt or relaunch elevated.
            // Must run before the shell mutex: the elevated copy takes the mutex,
            // this process only lingers as Winlogon's watched shell process.
            var handedOver = SelfElevation.EnsureElevatedIfConfigured(args);
            if (handedOver is not null)
            {
                return handedOver.Value;
            }
        }

        if (Mode == RunMode.Shell)
        {
            if (!AcquireShellMutex())
            {
                Log.Warn("Another WSGM shell instance is running; exiting.");
                return 0;
            }
            // Record this start BEFORE deciding, so the breaker fires on the
            // 3rd start within 2 minutes (this one included) as documented.
            CrashLoopBreaker.RecordStart();
            if (CrashLoopBreaker.IsLooping())
            {
                Log.Error("Crash loop detected (3+ shell starts within 2 minutes). " +
                          "Restoring previous shell and starting explorer.");
                ShellRegistration.Uninstall();
                ExplorerControl.StartExplorer();
                // Pin release first (invariant: fires on EVERY recovery path,
                // ahead of cosmetic restores) — same ordering as --restore-shell.
                SteamInputPin.ReleaseBestEffort("crash-loop");
                SlateMode.RestoreOriginal();
                RestoreDisplayScalesBestEffort();
                // Clear the marker so the next manual start isn't instantly disarmed.
                CrashLoopBreaker.Reset();
                return 1;
            }
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Panic("UnhandledException", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        UpdateExitWatcher.Start(() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Posted jobs only run once StartWithClassicDesktopLifetime pumps the
            // dispatcher, so the classic desktop lifetime is always in place here
            // and Shutdown() flows through the normal teardown below (pin release,
            // slate/scale restore). No Environment.Exit fallback: it would skip
            // that teardown, and if this ever failed to match, the installer's
            // taskkill fallback still ends the process.
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
            {
                lifetime.Shutdown();
            }
        }));

        try
        {
            var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            // Normal shutdown. Settings-only processes skip the reset unless they
            // pinned themselves (overlay test) — firing /0 from a stray settings
            // window would unpin a still-running shell.
            if (Mode is RunMode.Shell or RunMode.OverlayTest || SteamInputPin.IsApplied)
            {
                SteamInputPin.ReleaseBestEffort("shutdown");
            }
            if (Mode == RunMode.Shell)
            {
                SlateMode.RestoreOriginal();
                RestoreDisplayScalesBestEffort();
                // A clean exit is NOT a crash: without this, two update restarts
                // plus a sign-in inside 2 minutes read as a crash loop and disarm
                // the shell (device-observed). Only dirty deaths — which never
                // reach this line — may accumulate toward the breaker.
                CrashLoopBreaker.Reset();
            }
            return exitCode;
        }
        catch (Exception ex)
        {
            Panic("Avalonia lifetime crashed", ex);
            return 1;
        }
    }

    private static RunMode DecideMode(string[] args)
        => DecideMode(
            args,
            ShellRegistration.IsInstalledForThisExe(),
            Interop.NativeMethods.GetShellWindow() != 0 || ExplorerControl.IsRunningInSession());

    /// <summary>Resolves the requested mode from explicit flags or auto-mode state.
    /// The state is supplied separately so the precedence rules can be verified
    /// without querying the live shell from a test process.</summary>
    internal static RunMode DecideMode(string[] args, bool registeredAsShell, bool desktopAlive)
    {
        if (args.Contains("--shell", StringComparer.OrdinalIgnoreCase))
        {
            return RunMode.Shell;
        }

        if (args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
        {
            return RunMode.Settings;
        }

        if (args.Contains("--overlay-test", StringComparer.OrdinalIgnoreCase))
        {
            return RunMode.OverlayTest;
        }

        // Auto: we are the registered shell and no desktop is alive -> shell mode.
        return registeredAsShell && !desktopAlive ? RunMode.Shell : RunMode.Settings;
    }

    private static bool AcquireShellMutex()
    {
        _shellMutex = new Mutex(initiallyOwned: true, @"Local\WSGM.Shell", out var createdNew);
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

    /// <summary>Fatal-error handler for shell mode: restore the shell registration FIRST
    /// (so Winlogon's AutoRestartShell cannot resurrect us next to explorer), then bring
    /// the desktop back, then die.</summary>
    private static void Panic(string context, Exception? ex)
    {
        Log.Error($"PANIC ({context})", ex ?? new Exception("unknown"));
        if (Mode == RunMode.Shell)
        {
            ShellRegistration.Uninstall();
            // Best-effort (fails from a non-UI thread, and the dying process
            // destroys the window anyway): don't leave our Shell_TrayWnd up while
            // explorer's taskbar comes back.
            try
            {
                Shell.TrayHost.DestroyActive();
            }
            catch { /* recovery must not throw */ }
            ExplorerControl.StartExplorer();
            SlateMode.RestoreOriginal();
            RestoreDisplayScalesBestEffort();
        }
        // Same guard as normal shutdown: firing /0 from a crashing settings
        // process would unpin a still-running shell.
        if (Mode is RunMode.Shell or RunMode.OverlayTest || SteamInputPin.IsApplied)
        {
            SteamInputPin.ReleaseBestEffort("panic");
        }
    }

    /// <summary>Game mode forces 100% scaling and that persists in the registry —
    /// every way out of shell mode must put the captured values back.</summary>
    private static void RestoreDisplayScalesBestEffort()
    {
        try
        {
            DisplayScale.RestoreSaved(ConfigStore.Load());
        }
        catch
        {
            // Recovery paths must never be blocked by scaling cleanup.
        }
    }

    /// <summary>Builds the Avalonia application configuration used by all UI modes.</summary>
    /// <returns>The configured Avalonia application builder.</returns>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

/// <summary>Disarms WSGM if the shell process keeps dying at logon: 3 or more
/// shell-mode starts within 2 minutes restore the previous shell automatically.</summary>
internal static class CrashLoopBreaker
{
    private static string MarkerPath => Path.Combine(Log.Directory, "shell-starts.txt");

    public static void RecordStart()
    {
        try
        {
            File.AppendAllText(MarkerPath, DateTime.UtcNow.ToString("O") + Environment.NewLine);
        }
        catch { }
    }

    /// <summary>Call AFTER RecordStart so the current start counts toward the 3.</summary>
    public static bool IsLooping()
    {
        try
        {
            if (!File.Exists(MarkerPath))
            {
                return false;
            }

            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(2);
            var all = File.ReadAllLines(MarkerPath)
                .Select(l => DateTime.TryParse(l, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue)
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
                File.WriteAllLines(MarkerPath, all.Where(t => t > cutoff).Select(t => t.ToString("O")));
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Clears the marker after the breaker fired, so the next manual
    /// shell start begins with a clean slate instead of being disarmed again.</summary>
    public static void Reset()
    {
        try
        {
            File.Delete(MarkerPath);
        }
        catch { }
    }
}
