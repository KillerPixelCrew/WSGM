using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using WSGM.Themes;
using WSGM.UiTests.Infrastructure;
using Xunit.Sdk;
using Xunit.v3;

[assembly: AvaloniaTestApplication(typeof(TestApplication))]
[assembly: Parallelization(Mode = ParallelMode.None)]

namespace WSGM.UiTests.Infrastructure;

public sealed class TestApplication : App
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>()
        .WithInterFont()
        .With(new FontManagerOptions
        {
            FontFamilyMappings = new Dictionary<string, FontFamily>
            { ["Inter"] = new("avares://Avalonia.Fonts.Inter/Assets#Inter") }
        })
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public override void OnFrameworkInitializationCompleted()
    {
        // App.Initialize loads the actual resource graph. Its startup override owns live services.
        RequestedThemeVariant = ThemeVariant.Dark;
        AccentPalette.Apply(this, AccentPalette.Parse("#4CC2FF"));
    }
}
