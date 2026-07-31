using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WSGM.Core;
using WSGM.Settings;
using WSGM.Shell;

namespace WSGM;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            switch (Program.Mode)
            {
                case RunMode.Shell:
                    // No main window — the shell session runs headless until the
                    // overlay is summoned. Keep the app alive explicitly.
                    desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
                    var session = new ShellSession(ConfigStore.Load());
                    session.Start();
                    break;

                case RunMode.OverlayTest:
                    desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
                    var testSession = new ShellSession(ConfigStore.Load(), overlayTestOnly: true);
                    testSession.Start();
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
