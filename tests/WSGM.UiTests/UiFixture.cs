using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Settings;
using WSGM.Shell;
using WSGM.Themes;

namespace WSGM.UiTests;

internal sealed class UiFixture : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly ILogSink? _previousSink = Logger.Sink;
    private readonly BindingErrors _errors = new();
    private readonly List<Window> _windows = [];
    private readonly List<IDisposable> _owned = [];
    internal List<string> Calls { get; } = [];
    internal AppConfig Saved { get; private set; } = new() { AccentColor = "#4CC2FF", QuickSetupRevision = QuickSetup.CurrentRevision };
    internal Func<SettingsViewModel.SaveRequest, Task<SettingsViewModel.SaveResult>>? Persist { get; set; }
    internal OverlayWindow.SessionState Session { get; } = new();

    internal UiFixture()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        Logger.Sink = _errors;
        AccentPalette.Apply(Application.Current!, AccentPalette.Parse("#4CC2FF"));
    }

    internal SettingsWindow Settings(int width = 1280, int height = 800)
    {
        SettingsViewModel.SettingsServices services = new(
            () => [], () => [],
            () => Calls.Add("save-import-begin"), () => Calls.Add("save-import-end"),
            async request =>
            {
                Calls.Add("save");
                if (Persist is { } persist) { return await persist(request); }
                AppConfig fresh = ConfigStore.CloneJson(Saved, ConfigJsonContext.Default.AppConfig);
                SettingsViewModel.ApplyCapturedValues(fresh, request, request.Splash);
                Saved = fresh;
                return new(fresh, [], null);
            },
            _ => { Calls.Add("reconcile"); return Task.CompletedTask; },
            (message, _) => Calls.Add(message));
        var model = new SettingsViewModel(ConfigStore.CloneJson(Saved, ConfigJsonContext.Default.AppConfig),
            null, false, services);
        var windowServices = new SettingsWindowServices(new(),
            () => Calls.Add("input-start"), () => Calls.Add("input-stop"),
            () => Calls.Add("window-import-begin"), () => Calls.Add("window-import-end"),
            () => { Calls.Add("device-read"); return Task.CompletedTask; }, () => Saved.AccentColor);
        SettingsWindow window = new(model, windowServices) { Width = width, Height = height };
        Show(window);
        return window;
    }

    internal OverlayWindow Overlay(int width = 1280, int height = 800)
    {
        SystemStatus status = new();
        _owned.Add(status);
        OverlayWindow window = new(
            new OverlayViewModel { HomeAppName = "Steam", HomeAppAlive = true, ExplorerRunning = true },
            new AppSwitcherViewModel(), status, Session,
            w => { w.Width = width; w.Height = Math.Round(height * OverlayWindow.SheetHeightFraction); },
            _ => Calls.Add("tabs-sync"));
        window.SetPins(["home.steam", "home.desktop"]);
        Show(window);
        return window;
    }

    private void Show(Window window)
    {
        _windows.Add(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        foreach (var visual in window.GetVisualDescendants().OfType<Avalonia.Animation.Animatable>())
        {
            visual.Transitions = null;
        }
    }

    internal static T Named<T>(Control parent, string name) where T : Control =>
        parent.FindControl<T>(name) ?? throw new InvalidOperationException($"Missing control {name}");

    internal static Button Tab(Window window, int index) =>
        Named<TabStrip>(window, "Tabs").GetVisualDescendants().OfType<Button>().ElementAt(index);

    internal static void Click(Window window, Control control, MouseButton button = MouseButton.Left)
    {
        control.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Assert.True(control.IsEffectivelyVisible);
        Assert.True(control.IsEffectivelyEnabled);
        Point position = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("Control is not attached to the window");
        window.MouseMove(position);
        window.MouseDown(position, button);
        window.MouseUp(position, button);
    }

    internal static void Key(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        if (window.IsVisible) { window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null); }
    }

    public void Dispose()
    {
        try
        {
            foreach (Window window in _windows.AsEnumerable().Reverse()) { window.Close(); }
            foreach (IDisposable resource in _owned.AsEnumerable().Reverse()) { resource.Dispose(); }
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

    private sealed class BindingErrors : ILogSink
    {
        internal List<string> Messages { get; } = [];
        public bool IsEnabled(Avalonia.Logging.LogEventLevel level, string area) =>
            area == "Binding" && level >= Avalonia.Logging.LogEventLevel.Warning;
        public void Log(Avalonia.Logging.LogEventLevel level, string area, object? source, string messageTemplate) =>
            Messages.Add(messageTemplate);
        public void Log(Avalonia.Logging.LogEventLevel level, string area, object? source, string messageTemplate,
            params object?[] propertyValues) => Messages.Add(messageTemplate + " " + string.Join(", ", propertyValues));
    }
}
