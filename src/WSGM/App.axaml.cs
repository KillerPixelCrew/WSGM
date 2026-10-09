using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LibHandheld;
using WSGM.Core;
using WSGM.Install;
using WSGM.Settings;
using WSGM.Shell;
using WSGM.Themes;

namespace WSGM;

/// <summary>Configures Avalonia application lifetime and creates the selected WSGM session.</summary>
public class App : Application
{
    private readonly StartupOptions _options;
    private readonly ConfigReadResult _startupRead;
    private readonly SteamInputBlocker _steamInput;
    private readonly ConfigStore _store;

    // Deliberate root for the headless shell session — without it the session
    // (and its config watcher) would survive only via incidental GC reachability.
    private ShellSession? _session;

    /// <summary>Creates the application over the configuration read during process startup.</summary>
    /// <param name="startupRead">
    ///     The entry point's configuration read; a failed read runs on defaults, and Settings says so.
    /// </param>
    /// <param name="store">The process-owned configuration persistence.</param>
    /// <param name="options">The immutable options parsed by the process entry point.</param>
    /// <param name="steamInput">The process's Steam Input lease owner, which the entry point releases at exit.</param>
    internal App(ConfigReadResult startupRead, ConfigStore store, StartupOptions options, SteamInputBlocker steamInput)
    {
        _startupRead = startupRead ?? throw new ArgumentNullException(nameof(startupRead));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _steamInput = steamInput ?? throw new ArgumentNullException(nameof(steamInput));
    }

    /// <summary>The single exit coordinator, initialized when the desktop application lifetime is composed.</summary>
    internal ApplicationRuntime Runtime { get; private set; } = null!;

    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        // Apply the accent before creating any window to avoid a differently styled first frame.
        var config = _startupRead.Config ?? new AppConfig();
        AccentPalette.Apply(this, AccentPalette.Parse(config.AccentColor));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var verboseLogging = _options.Verbose;
            switch (_options.Mode)
            {
                case RunMode.Shell:
                    // No main window — the shell session runs headless until the
                    // overlay is summoned. Keep the app alive explicitly.
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    _session = new ShellSession(config, _store, _steamInput, serviceBoot: _options.ServiceBoot,
                        desktopResident: _options.DesktopResident, verboseLogging: verboseLogging);
                    break;

                case RunMode.OverlayTest:
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    _session = new ShellSession(config, _store, _steamInput, true,
                        verboseLogging: verboseLogging);
                    break;

                case RunMode.Settings:
                default:
                    // Standalone Settings has no plugin host; saved plugin actions remain read-only.
                    var settings = SettingsViewModel.FromLoadedConfig(_startupRead, _store, _steamInput.Shim, () => [],
                        HandheldDevice.Detect(DeviceMachineIdentity.Collect()));
                    desktop.MainWindow = new SettingsWindow(settings,
                        SettingsWindowServices.Create(settings, _steamInput, null,
                            SettingsSurface.TestSheet(_store, _steamInput, null)));
                    break;
            }

            Runtime = new ApplicationRuntime(_session is null ? null : _session.ShutdownAsync,
                code => { desktop.Shutdown(code); }, UpdateExitWatcher.ReportHandoff);
            desktop.ShutdownRequested += OnShutdownRequested;
            if (_options.Mode is RunMode.Shell)
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
