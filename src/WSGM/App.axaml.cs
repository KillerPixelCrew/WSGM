using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Settings;
using WSGM.Shell;
using WSGM.Themes;

namespace WSGM;

/// <summary>Configures Avalonia application lifetime and creates the selected WSGM session.</summary>
public class App : Application
{
    private readonly AppConfig _startupConfig;
    private readonly ConfigStore _store;

    // Deliberate root for the headless shell session — without it the session
    // (and its config watcher) would survive only via incidental GC reachability.
    private ShellSession? _session;

    /// <summary>Creates the application over the configuration loaded during process startup.</summary>
    /// <param name="startupConfig">The configuration loaded by the process entry point.</param>
    /// <param name="store">The process-owned configuration persistence.</param>
    public App(AppConfig startupConfig, ConfigStore store)
    {
        _startupConfig = startupConfig ?? throw new ArgumentNullException(nameof(startupConfig));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    internal ApplicationRuntime Runtime { get; private set; } = null!;

    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        // Accent first, before any window exists — every mode (shell, overlay
        // test, settings, welcome) shows the configured accent from first paint.
        var config = _startupConfig;
        AccentPalette.Apply(this, AccentPalette.Parse(config.AccentColor));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            switch (Program.Mode)
            {
                case RunMode.Shell:
                    // No main window — the shell session runs headless until the
                    // overlay is summoned. Keep the app alive explicitly.
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    _session = new ShellSession(config, _store, serviceBoot: Program.ServiceBoot,
                        desktopResident: Program.DesktopResident);
                    break;

                case RunMode.OverlayTest:
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    _session = new ShellSession(config, _store, true);
                    break;

                case RunMode.Settings:
                default:
                    // Setup is the only installer, so there is no portable run to offer
                    // an install for, and it asks every first-run question itself.
                    desktop.MainWindow = new SettingsWindow(SettingsViewModel.FromLoadedConfig(config, _store));
                    break;
            }

            Runtime = new ApplicationRuntime(_session is null ? null : _session.ShutdownAsync,
                code => { desktop.Shutdown(code); }, UpdateExitWatcher.ReportHandoff);
            desktop.ShutdownRequested += OnShutdownRequested;
            if (Program.Mode is RunMode.Shell)
            {
                Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
            }

            if (_session is not null)
            {
                _ = ObserveSessionStartupAsync(_session.StartAsync());
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task ObserveSessionStartupAsync(Task startup)
    {
        try
        {
            await startup;
        }
        catch (OperationCanceledException) when (Runtime.ExitRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Shell session startup failed", ex);
            await Runtime.StartupFailedExit();
        }
    }

    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        if (eventArgs.Exception is OutOfMemoryException)
        {
            return;
        }

        Log.Error("Shell dispatcher callback failed", eventArgs.Exception);
        eventArgs.Handled = true;
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.Normal);
        _ = Runtime.RequestExit();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs eventArgs)
    {
        var exit = Runtime.RequestOsSessionEnd();
        if (!exit.IsCompleted)
        {
            DispatcherFrame frame = new();
            _ = EndFrameAsync(exit, frame);
            Dispatcher.UIThread.PushFrame(frame);
        }

        // Avalonia owns the OS exit after this synchronous handler returns. Never veto session end.
        eventArgs.Cancel = false;
    }

    private static async Task EndFrameAsync(Task exit, DispatcherFrame frame)
    {
        try
        {
            await exit;
        }
        finally
        {
            frame.Continue = false;
        }
    }
}
