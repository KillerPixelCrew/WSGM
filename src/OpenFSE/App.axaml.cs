using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OpenFSE.Core;
using OpenFSE.Settings;
using OpenFSE.Shell;

namespace OpenFSE;

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
                    desktop.MainWindow = new SettingsWindow();
                    break;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
