using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using WSGM.Core;
using WSGM.Themes;
using WSGM.UiTests.Infrastructure;

[assembly: AvaloniaTestApplication(typeof(TestApplication))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WSGM.UiTests.Infrastructure;

public sealed class TestApplication : App
{
    // AppBuilder.Configure<T> constructs the application itself, so the headless harness needs a
    // parameterless entry point. Default configuration is the right one to hand it: these tests
    // exercise views, and OnFrameworkInitializationCompleted below never starts the live services
    // that would read it. The store points at an unused temporary root and a private mutex, so
    // nothing here can reach the real user data or the production config lock.
    public TestApplication()
        : base(new AppConfig(),
            new ConfigStore(new UserDataContext(
                Path.Combine(Path.GetTempPath(), "wsgm-ui-app-" + Guid.NewGuid().ToString("N")),
                @"Local\WSGM.UiTests.App." + Guid.NewGuid().ToString("N"))),
            StartupOptions.Parse([]))
    {
        // The build revision is the commit count, so a baseline showing it would go stale with the next
        // commit. The release version stays, as the update status shows it too.
        Credits.VersionText = Credits.Describe(UpdateChecker.CurrentVersion);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<TestApplication>()
            .WithInterFont()
            .With(new FontManagerOptions
            {
                FontFamilyMappings = new Dictionary<string, FontFamily>
                    { ["Inter"] = new("avares://Avalonia.Fonts.Inter/Assets#Inter") }
            })
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // App.Initialize loads the actual resource graph. Its startup override owns live services.
        RequestedThemeVariant = ThemeVariant.Dark;
        AccentPalette.Apply(this, AccentPalette.Parse("#4CC2FF"));
    }
}
