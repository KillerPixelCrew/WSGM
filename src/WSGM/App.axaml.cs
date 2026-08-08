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
        var config = ConfigStore.Load();
        Themes.AccentPalette.Apply(this, Themes.AccentPalette.Parse(config.AccentColor));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            switch (Program.Mode)
            {
                case RunMode.Shell:
                    // No main window — the shell session runs headless until the
                    // overlay is summoned. Keep the app alive explicitly.
                    desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
                    _session = new ShellSession(config, serviceBoot: Program.ServiceBoot);
                    _session.Start();
                    break;

                case RunMode.OverlayTest:
                    desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
                    _session = new ShellSession(config, overlayTestOnly: true);
                    _session.Start();
                    break;

                case RunMode.Settings:
                default:
                    // Running portable (not from the install dir)? Offer the friendly
                    // install/update dialog first — CLI flags are for scripts only.
                    desktop.MainWindow = Core.Installer.IsRunningFromInstallDir
                        ? new SettingsWindow()
                        : new WelcomeWindow();
                    break;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
