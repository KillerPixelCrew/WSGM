using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WindowsDeviceControl;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Input;
using WSGM.Overlay;
using WSGM.Settings;
using WSGM.Shell;
using WSGM.Themes;

namespace WSGM.UiTests.Infrastructure;

internal sealed class UiFixture : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly BindingErrors _errors = new();
    private readonly List<IDisposable> _owned = [];
    private readonly ILogSink? _previousSink = Logger.Sink;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly List<Window> _windows = [];

    internal UiFixture()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        Logger.Sink = _errors;
        AccentPalette.Apply(Application.Current!, AccentPalette.Parse("#4CC2FF"));
    }

    internal List<string> Calls { get; } = [];

    internal AppConfig Saved { get; private set; } = new()
        { AccentColor = "#4CC2FF" };

    internal Func<SettingsViewModel.SaveRequest, Task<SettingsViewModel.SaveResult>>? Persist { get; set; }
    internal Action<string> ClaimSteamInput { get; set; } = _ => { };
    internal Action<string> AcquireSteamInput { get; set; } = _ => { };
    internal Action<string, string> ReleaseSteamInput { get; set; } = (_, _) => { };
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
    internal IReadOnlyList<SettingsViewModel.PluginActionOption> PluginActions { get; set; } = [];

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
        }
    }

    internal SettingsWindow Settings(int width = 1280, int height = 800, bool gameModeSurface = false)
    {
        SettingsViewModel.SettingsServices services = new(
            () => ReadDisplays?.Invoke() ?? Displays,
            target => DisplayFacts.GetValueOrDefault(target.DevicePath),
            () => PluginActions,
            () => [],
            () => Calls.Add("save-import-begin"), () => Calls.Add("save-import-end"),
            async request =>
            {
                Calls.Add("save");
                if (Persist is { } persist)
                {
                    return await persist(request);
                }

                var fresh = ConfigStore.CloneJson(Saved, ConfigJsonContext.Default.AppConfig);
                // The merge returns the configuration to persist rather than mutating the fresh
                // load, so the result is what gets saved. Keeping `fresh` here stored the on-disk
                // state back over itself and dropped every edit the test had just made.
                var merged = SettingsViewModel.ApplyCapturedValues(fresh, request, request.Splash);
                Saved = merged;
                return new SettingsViewModel.SaveResult(merged, [], null);
            },
            _ =>
            {
                Calls.Add("reconcile");
                return Task.CompletedTask;
            },
            (message, _) => Calls.Add(message),
            // A fixed report: the real reader describes this machine's last standby, which put the
            // previous night's sleep length into the settings-system baselines.
            () => new ModernStandbyReport(true, "This machine has not been in standby since it booted.", []),
            () => ScanSteamAutostart(), sources => ApplySteamAutostart(sources),
            // A fixed observation: the real reader describes whatever this machine has plugged in,
            // which would put the local endpoint count into every settings baseline.
            _ => Audio);
        var model = new SettingsViewModel(ConfigStore.CloneJson(Saved, ConfigJsonContext.Default.AppConfig),
            null, false, services);
        var windowServices = new SettingsWindowServices(new GamepadService(),
            () => Calls.Add("input-start"), () => Calls.Add("input-stop"),
            () => Calls.Add("window-import-begin"), () => Calls.Add("window-import-end"),
            () =>
            {
                Calls.Add("device-read");
                return Task.CompletedTask;
            }, () => Saved.AccentColor,
            owner => ClaimSteamInput(owner), owner => AcquireSteamInput(owner),
            (owner, reason) => ReleaseSteamInput(owner, reason));
        SettingsWindow window = new(model, windowServices, gameModeSurface) { Width = width, Height = height };
        Show(window);
        return window;
    }

    internal OverlayWindow Overlay(int width = 1280, int height = 800, double uiScale = 1.0, double renderScale = 1.0)
    {
        SystemStatus status = new();
        _owned.Add(status);
        OverlayWindow window = new(
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
            new AppSwitcherViewModel(), status, Session,
            w =>
            {
                w.Width = width / renderScale;
                w.Height = height / renderScale;
                var factor = OverlayWindow.ComputeContentScale(uiScale, renderScale, width, height);
                Named<LayoutTransformControl>(w, "RootScale").LayoutTransform = new ScaleTransform(factor, factor);
            },
            _ => Calls.Add("tabs-sync"), uiScale);
        window.SetPins(["home.steam", "home.desktop"]);
        Show(window);
        return window;
    }

    private void Show(Window window)
    {
        _windows.Add(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        foreach (var visual in window.GetVisualDescendants().OfType<Animatable>())
        {
            visual.Transitions = null;
        }
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
