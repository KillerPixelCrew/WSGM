using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WSGM.Core;
using WSGM.Settings;
using WSGM.Shell;

namespace WSGM;

/// <summary>Configures Avalonia application lifetime and creates the selected WSGM session.</summary>
public class App : Application
{
    // Deliberate root for the headless shell session — without it the session
    // (and its config watcher) would survive only via incidental GC reachability.
    private ShellSession? _session;
    // Installer rollback can be recovering from an orphaned DeviceHost. Keep a second handle to
    // setup's unowned global marker for this process's complete lifetime, including Settings mode.
    private Mutex? _installerRollbackOwnerReservation;
    private bool _shutdownInProgress;
    private bool _sessionStopped;
    private ApplicationShutdownOutcome? _shutdownOutcome;

    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (Program.InstallerRollbackWithoutDeviceIntegration)
        {
            _installerRollbackOwnerReservation = DeviceCoordinator.TryRetainOwnerMutex(
                DeviceCoordinator.ProductionOwnerName);
            if (_installerRollbackOwnerReservation is not null)
            {
                Program.ReportInstallerRollbackOwnerRetained();
            }
            else
            {
                Log.Error("Installer rollback could not retain the machine-wide device-owner marker.");
            }
        }

        // Accent first, before any window exists — every mode (shell, overlay
        // test, settings, welcome) shows the configured accent from first paint.
        var config = ConfigStore.Load();
        Themes.AccentPalette.Apply(this, Themes.AccentPalette.Parse(config.AccentColor));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownRequested += OnShutdownRequested;
            switch (Program.Mode)
            {
                case RunMode.Shell:
                    // No main window — the shell session runs headless until the
                    // overlay is summoned. Keep the app alive explicitly.
                    desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
                    _session = new ShellSession(
                        config,
                        serviceBoot: Program.ServiceBoot,
                        suppressDeviceIntegration: Program.InstallerRollbackWithoutDeviceIntegration);
                    _ = ObserveSessionStartupAsync(_session.StartAsync(), desktop);
                    break;

                case RunMode.OverlayTest:
                    desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
                    _session = new ShellSession(config, overlayTestOnly: true);
                    _ = ObserveSessionStartupAsync(_session.StartAsync(), desktop);
                    break;

                case RunMode.Settings:
                default:
                    // Inno is the only installer, so there is no portable run to offer
                    // an install for. First-run onboarding is Quick Setup, which the
                    // Settings window raises over itself.
                    desktop.MainWindow = new SettingsWindow();
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
            Environment.ExitCode = 1;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => desktop.Shutdown());
        }
    }

    private async void OnShutdownRequested(
        object? sender,
        ShutdownRequestedEventArgs eventArgs)
    {
        if (_shutdownInProgress)
        {
            eventArgs.Cancel = true;
            return;
        }

        ApplicationShutdownReason reason = ApplicationShutdownRequest.Consume();
        if (_session is null || _sessionStopped)
        {
            UpdateExitWatcher.ReportHandoff(
                reason,
                _shutdownOutcome ?? ApplicationShutdownOutcome.Clean);
            return;
        }

        eventArgs.Cancel = true;
        _shutdownInProgress = true;
        ApplicationShutdownOutcome outcome = ApplicationShutdownOutcome.Failed;
        try
        {
            outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
                deadline => _session.ShutdownAsync(reason, deadline),
                reason);
        }
        finally
        {
            _shutdownOutcome = outcome;
            UpdateExitWatcher.ReportHandoff(reason, outcome);
            _sessionStopped = true;
            _shutdownInProgress = false;
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
    }
}
