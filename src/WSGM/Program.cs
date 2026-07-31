using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using WSGM.Core;

namespace WSGM;

public enum RunMode
{
    Shell,
    Settings,
    OverlayTest,
}

public static class Program
{
    public static RunMode Mode { get; private set; } = RunMode.Settings;
    private static Mutex? _shellMutex;

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

        if (Mode is RunMode.Shell or RunMode.OverlayTest)
        {
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
            if (CrashLoopBreaker.IsLooping())
            {
                Log.Error("Crash loop detected (3+ shell starts within 2 minutes). " +
                          "Restoring previous shell and starting explorer.");
                ShellRegistration.Uninstall();
                ExplorerControl.StartExplorer();
                SteamInputPin.ReleaseBestEffort("crash-loop");
                return 1;
            }
            CrashLoopBreaker.RecordStart();
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
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
            {
                lifetime.Shutdown();
            }
            else
            {
                SteamInputPin.ReleaseBestEffort("update-exit");
                Environment.Exit(0);
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
    {
        if (args.Contains("--shell", StringComparer.OrdinalIgnoreCase)) return RunMode.Shell;
        if (args.Contains("--settings", StringComparer.OrdinalIgnoreCase)) return RunMode.Settings;
        if (args.Contains("--overlay-test", StringComparer.OrdinalIgnoreCase)) return RunMode.OverlayTest;

        // Auto: we are the registered shell and no desktop is alive -> shell mode.
        var registered = ShellRegistration.IsInstalledForThisExe();
        var desktopAlive = Interop.NativeMethods.GetShellWindow() != 0 || ExplorerControl.IsRunningInSession();
        return registered && !desktopAlive ? RunMode.Shell : RunMode.Settings;
    }

    private static bool AcquireShellMutex()
    {
        _shellMutex = new Mutex(initiallyOwned: true, @"Local\WSGM.Shell", out var createdNew);
        return createdNew;
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
            ExplorerControl.StartExplorer();
            // The desktop we just brought back should behave like a touch desktop.
            SlateMode.ApplyDesktopMode();
        }
        SteamInputPin.ReleaseBestEffort("panic");
    }

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

    public static bool IsLooping()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return false;
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(2);
            var recent = File.ReadAllLines(MarkerPath)
                .Select(l => DateTime.TryParse(l, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue)
                .Count(t => t > cutoff);
            if (recent >= 3) return true;
            // Trim the file so it doesn't grow forever.
            if (recent == 0) File.Delete(MarkerPath);
            return false;
        }
        catch
        {
            return false;
        }
    }
}
