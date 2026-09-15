using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using WSGM.Themes;

[assembly: AvaloniaTestApplication(typeof(WSGM.UiTests.TestApplication))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WSGM.UiTests;

public sealed class TestApplication : App
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>()
        .WithInterFont()
        .With(new Avalonia.Media.FontManagerOptions
        {
            FontFamilyMappings = new Dictionary<string, Avalonia.Media.FontFamily>
            { ["Inter"] = new("avares://Avalonia.Fonts.Inter/Assets#Inter") },
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
