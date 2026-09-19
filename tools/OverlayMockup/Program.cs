using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace WSGM.OverlayMockup;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AppBuilder.Configure<MockupApp>().UsePlatformDetect().WithInterFont()
            .StartWithClassicDesktopLifetime(args);
    }
}

internal sealed class MockupApp : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MockupWindow(desktop.Args ?? []);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
