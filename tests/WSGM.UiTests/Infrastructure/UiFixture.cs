using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WindowsDeviceControl;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Input;
using WSGM.Overlay;
using WSGM.Settings;
using WSGM.Shell;
using WSGM.Testing;
using WSGM.Themes;

namespace WSGM.UiTests.Infrastructure;

internal sealed class UiFixture : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly BindingErrors _errors = new();
    private readonly List<IDisposable> _owned = [];
    private readonly ILogSink? _previousSink = Logger.Sink;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wsgm-ui-" + Guid.NewGuid().ToString("N"));
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly List<Window> _windows = [];

    internal UiFixture()
    {
        Store = new ConfigStore(new UserDataContext(_root, @"Local\WSGM.UiTests." + Guid.NewGuid().ToString("N")));
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        Logger.Sink = _errors;
        AccentPalette.Apply(Application.Current!, AccentPalette.Parse("#4CC2FF"));
    }

    internal List<string> Calls { get; } = [];
    internal ConfigStore Store { get; }

    /// <summary>The snapshots the Settings window's test sheet was shown with, in order.</summary>
    internal List<AppConfig> TestSheets { get; } = [];

    internal AppConfig Saved { get; private set; } = new()
        { AccentColor = "#4CC2FF" };

    internal Func<SettingsViewModel.SaveRequest, Task<SettingsViewModel.SaveResult>>? Persist { get; set; }
    internal Action<string> HoldSteamInput { get; set; } = _ => { };
    internal Action<string, string> DropSteamInput { get; set; } = (_, _) => { };
    internal Func<IReadOnlyList<SteamAutostartSource>> ScanSteamAutostart { get; set; } = () => [];

    internal Func<IReadOnlyList<SteamAutostartSource>, SteamAutostartTakeoverResult> ApplySteamAutostart { get; set; } =
        _ => throw new InvalidOperationException("Unexpected Steam autostart write.");

    /// <summary>
    ///     What a Snapshot in Settings observes. Synthetic: these tests never read this
    ///     machine's displays.
    /// </summary>
    internal DisplayArrangement Displays { get; set; } =
        new([], "no-displays", DateTimeOffset.UnixEpoch);

    /// <summary>What the audio profile editors observe. Nothing, unless a test says otherwise.</summary>
    internal AudioDiscovery Audio { get; set; } = AudioDiscovery.Empty;

    internal Func<DisplayArrangement>? ReadDisplays { get; set; }

    /// <summary>What each display claims to support, keyed by device path.</summary>
    internal Dictionary<string, DisplayCatalogFacts> DisplayFacts { get; } = [];

    /// <summary>
    ///     The actions a running plugin would declare. Empty means no plugin host, which is
    ///     what a standalone Settings process sees.
    /// </summary>
    internal IReadOnlyList<PluginActionOption> PluginActions { get; set; } = [];

    private OverlayWindow.SessionState Session { get; } = new();

    public void Dispose()
    {
        try
        {
            foreach (var window in _windows.AsEnumerable().Reverse())
            {
                window.Close();
            }

            foreach (var resource in _owned.AsEnumerable().Reverse())
            {
                resource.Dispose();
            }

            Dispatcher.UIThread.RunJobs();
            Assert.True(_errors.Messages.Count == 0, string.Join("\n", _errors.Messages));
        }
        finally
        {
            Logger.Sink = _previousSink;
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _uiCulture;
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
    }

    internal SettingsWindow Settings(int width = 1280, int height = 800, bool gameModeSurface = false)
    {
        // Inert by default, so nothing here reads this machine; the fixture's own seams on top.
        var services = SettingsTestServices.Inert(Saved, Calls) with
        {
            CaptureDisplays = () => ReadDisplays?.Invoke() ?? Displays,
            ReadDisplayFacts = target => DisplayFacts.GetValueOrDefault(target.DevicePath),
            ReadPluginActions = () => PluginActions,
            BeginImportSession = () => Calls.Add("save-import-begin"),
            EndImportSession = () => Calls.Add("save-import-end"),
            Persist = async request =>
            {
                Calls.Add("save");
                if (Persist is { } persist)
                {
                    return await persist(request);
                }

                var fresh = ConfigJson.Clone(Saved, ConfigJsonContext.Default.AppConfig);
                // Use the production fresh-load merge so fixture saves exercise the same field
                // ownership and captured-edit tracking as the application.
                var (merged, changes) = SettingsSaveMerge.Apply(fresh, request, request.Splash);
                Saved = merged;
                return new SettingsViewModel.SaveResult(merged, [], null, changes);
            },
            LoadPersisted = () => ConfigJson.Clone(Saved, ConfigJsonContext.Default.AppConfig),
            ScanSteamAutostart = () => ScanSteamAutostart(),
            ApplySteamAutostart = sources => ApplySteamAutostart(sources),
            // Repair offered, as on an installed machine, so the Plugins page keeps its button.
            RepairAvailable = () => true,
            // A fixed observation: the real reader describes whatever this machine has plugged in,
            // which would put the local endpoint count into every settings baseline.
            ReadAudio = _ => Audio
        };
        var model = new SettingsViewModel(ConfigJson.Clone(Saved, ConfigJsonContext.Default.AppConfig),
            null, false, services, Store);
        var windowServices = new SettingsWindowServices(new GamepadService(),
            () => Calls.Add("input-start"), () => Calls.Add("input-stop"),
            () => Calls.Add("window-import-begin"), () => Calls.Add("window-import-end"),
            () =>
            {
                Calls.Add("device-read");
                return Task.CompletedTask;
            }, () => model.SavedAccentColor,
            owner => HoldSteamInput(owner), (owner, reason) => DropSteamInput(owner, reason),
            config =>
            {
                Calls.Add("test-sheet");
                TestSheets.Add(config);
                return new TestSheetHandle(Calls);
            });
        SettingsWindow window = new(model, windowServices) { Width = width, Height = height };
        if (gameModeSurface)
        {
            window.IncludeAsSwitchable();
        }

        // Display discovery starts as the window opens and reads on a worker, as in production; the
        // window is captured once its first-render rows are in.
        Show(window, () => model.ReadingDisplays);
        return window;
    }

    internal OverlayWindow Overlay(int width = 1280, int height = 800, double uiScale = 1.0, double renderScale = 1.0,
        OverlayWindow.SessionState? session = null)
    {
        AudioManager audio = new();
        RadioManager radios = new();
        RemovableDriveManager drives = new();
        _owned.Add(audio);
        _owned.Add(radios);
        _owned.Add(drives);
        SystemStatus status = new(audio, radios, drives);
        _owned.Add(status);
        OverlayWindow window = new(
            Store,
            new OverlayViewModel
            {
                HomeAppName = "Steam", HomeAppAlive = true, ExplorerRunning = true, ShowKeepAwake = true,
                PowerTimeoutValues = new Dictionary<PowerTimeoutKind, int?>
                {
                    [PowerTimeoutKind.DisplayDc] = 300,
                    [PowerTimeoutKind.DisplayAc] = 600,
                    [PowerTimeoutKind.SleepDc] = 900,
                    [PowerTimeoutKind.SleepAc] = 1800
                }
            },
            new AppSwitcherViewModel(), status, session ?? Session,
            w =>
            {
                w.Width = width / renderScale;
                w.Height = height / renderScale;
                w.ApplyContentScale(OverlayWindow.ComputeContentScale(uiScale, renderScale, width, height));
            }, uiScale);
        window.SetPins(["home.steam", "home.desktop"]);
        window.RefreshWindowsPolicies(false, false, false);
        Show(window);
        return window;
    }

    private void Show(Window window, Func<bool>? busy = null)
    {
        _windows.Add(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var started = Stopwatch.GetTimestamp();
        while (busy?.Invoke() == true)
        {
            if (Stopwatch.GetElapsedTime(started) > AsyncConditions.TimeLimit)
            {
                throw new TimeoutException("The window's opening work did not finish.");
            }

            Thread.Yield();
            Dispatcher.UIThread.RunJobs();
        }

        foreach (var visual in window.GetVisualDescendants().OfType<Animatable>())
        {
            visual.Transitions = null;
        }
    }

    internal static DisplayModeAccess ReadOnlyDisplayModes(DisplayModeSnapshot snapshot)
    {
        return new DisplayModeAccess(source => Task.FromResult(
                string.Equals(source, snapshot.Path.SourceName, StringComparison.OrdinalIgnoreCase) ? snapshot : null),
            (_, _) => throw new InvalidOperationException("A UI fixture must not apply a display mode."));
    }

    internal static T Named<T>(Control parent, string name) where T : Control
    {
        return parent.FindControl<T>(name) ?? throw new InvalidOperationException($"Missing control {name}");
    }

    internal static Button Tab(Window window, int index)
    {
        return Named<TabStrip>(window, "Tabs").GetVisualDescendants().OfType<Button>().ElementAt(index);
    }

    internal static Button Rail(OverlayWindow window, string key)
    {
        return Named<StackPanel>(window, "SectionRail").Children.OfType<Button>()
            .Single(button => Equals(button.Tag, "rail." + key));
    }

    internal static Button Rail(OverlayWindow window, OverlayPage page)
    {
        return Rail(window, page.ToString());
    }

    /// <summary>Opens every visible folded section in the window, or below one control.</summary>
    /// <param name="window">The window the clicks go to.</param>
    /// <param name="scope">
    ///     Limits the sections to this control's descendants. Its scroller is put back where it was, so a
    ///     capture shows the viewport the test arranged rather than the last heading clicked.
    /// </param>
    internal static void OpenSections(Window window, Control? scope = null)
    {
        var scroll = scope?.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        var offset = scroll?.Offset;
        foreach (var section in (scope ?? window).GetVisualDescendants().OfType<CollapsibleSection>()
                 .Where(section => section.IsEffectivelyVisible && !section.IsExpanded).ToArray())
        {
            // Expander's page transition is separate from Animatable.Transitions and otherwise
            // leaves a capture midway through the body's fade-in.
            foreach (var expander in section.GetVisualDescendants().OfType<Expander>())
            {
                expander.ContentTransition = null;
            }

            Click(window, section.Heading);
            Assert.True(section.IsExpanded);
            Dispatcher.UIThread.RunJobs();
        }

        if (scroll is not null && offset is { } previous)
        {
            scroll.Offset = previous;
            Dispatcher.UIThread.RunJobs();
        }
    }

    internal static void Click(Window window, Control control, MouseButton button = MouseButton.Left)
    {
        control.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Assert.True(control.IsEffectivelyVisible);
        Assert.True(control.IsEffectivelyEnabled);
        var position = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                       ?? throw new InvalidOperationException("Control is not attached to the window");
        window.MouseMove(position);
        window.MouseDown(position, button);
        window.MouseUp(position, button);
    }

    internal static void Key(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        if (window.IsVisible)
        {
            window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        }
    }

    /// <summary>A recorded test sheet; closing it is recorded too.</summary>
    private sealed class TestSheetHandle(List<string> calls) : IDisposable
    {
        public void Dispose()
        {
            calls.Add("test-sheet-closed");
        }
    }

    private sealed class BindingErrors : ILogSink
    {
        internal List<string> Messages { get; } = [];

        public bool IsEnabled(LogEventLevel level, string area)
        {
            return area == "Binding" && level >= LogEventLevel.Warning;
        }

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            Messages.Add(messageTemplate);
        }

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate,
            params object?[] propertyValues)
        {
            Messages.Add(messageTemplate + " " + string.Join(", ", propertyValues));
        }
    }
}
