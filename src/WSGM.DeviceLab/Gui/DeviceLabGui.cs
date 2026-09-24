using Avalonia;

namespace WSGM.DeviceLab.Gui;

internal static class DeviceLabGui
{
    /// <summary>The wizard options when the process runs the wizard, or null for the developer tabs.</summary>
    internal static WizardOptions? Wizard { get; private set; }

    internal static int Run(string[] args)
    {
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    internal static int RunWizard(WizardOptions options)
    {
        Wizard = options;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
    }

    private static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
