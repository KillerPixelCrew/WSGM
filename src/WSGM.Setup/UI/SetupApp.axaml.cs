using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace WSGM.Setup.UI;

/// <summary>The setup application.</summary>
internal sealed class SetupApp : Application
{
    /// <summary>The parsed command line, set before Avalonia starts.</summary>
    internal static SetupOptions Options { get; set; } = new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new SetupWindow { DataContext = new SetupViewModel(Options) };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
