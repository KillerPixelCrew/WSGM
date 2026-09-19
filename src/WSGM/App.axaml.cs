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

    // Deliberate root for the headless shell session — without it the session
    // (and its config watcher) would survive only via incidental GC reachability.
    private ShellSession? _session;
    private bool _sessionStopped;
    private bool _shutdownInProgress;
    private ApplicationShutdownOutcome? _shutdownOutcome;

    /// <summary>Creates the application over the configuration loaded during process startup.</summary>
    /// <param name="startupConfig">The configuration loaded by the process entry point.</param>
    public App(AppConfig startupConfig)
    {
        _startupConfig = startupConfig ?? throw new ArgumentNullException(nameof(startupConfig));
    }

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
            desktop.ShutdownRequested += OnShutdownRequested;
            switch (Program.Mode)
            {
                case RunMode.Shell:
                    // No main window — the shell session runs headless until the
                    // overlay is summoned. Keep the app alive explicitly.
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    _session = new ShellSession(config, serviceBoot: Program.ServiceBoot,
                        desktopResident: Program.DesktopResident);
                    _ = ObserveSessionStartupAsync(_session.StartAsync(), desktop);
                    break;

                case RunMode.OverlayTest:
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    _session = new ShellSession(config, true);
                    _ = ObserveSessionStartupAsync(_session.StartAsync(), desktop);
                    break;

                case RunMode.Settings:
                default:
                    // Inno is the only installer, so there is no portable run to offer
                    // an install for. First-run onboarding is Quick Setup, which the
                    // Settings window raises over itself.
                    desktop.MainWindow = new SettingsWindow(SettingsViewModel.FromLoadedConfig(config));
                    break;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task ObserveSessionStartupAsync(
        Task startup,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            await startup;
        }
        catch (OperationCanceledException) when (_shutdownInProgress || _sessionStopped)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Shell session startup failed", ex);
            await Dispatcher.UIThread.InvokeAsync(() => desktop.Shutdown(1));
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod
    private async void OnShutdownRequested(
        object? sender,
        ShutdownRequestedEventArgs eventArgs)
    {
        if (_shutdownInProgress)
        {
            eventArgs.Cancel = true;
            return;
        }

        var reason = ApplicationShutdownRequest.Consume();
        if (_session is null || _sessionStopped)
        {
            UpdateExitWatcher.ReportHandoff(
                reason,
                _shutdownOutcome ?? ApplicationShutdownOutcome.Clean);
            return;
        }

        eventArgs.Cancel = true;
        _shutdownInProgress = true;
        var outcome = ApplicationShutdownOutcome.Failed;
        try
        {
            outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
                deadline => _session.ShutdownAsync(reason, deadline),
                reason);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // ShutdownRequested is necessarily an async-void framework boundary.
            // Nothing may escape it: an unexpected cleanup fault must still report
            // a failed handoff and terminate with the failure exit code.
            Log.Error("Application shutdown failed", ex);
            outcome = ApplicationShutdownOutcome.Failed;
        }
        finally
        {
            _shutdownOutcome = outcome;
            UpdateExitWatcher.ReportHandoff(reason, outcome);
            _sessionStopped = true;
            _shutdownInProgress = false;
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown(ApplicationShutdownCoordinator.ExitCodeFor(outcome));
            }
        }
    }
}
