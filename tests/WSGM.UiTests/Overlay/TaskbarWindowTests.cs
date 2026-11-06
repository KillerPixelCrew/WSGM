using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Core;
using WSGM.Input;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.Tests.Fakes;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Overlay;

public sealed class TaskbarWindowTests
{
    [AvaloniaTheory]
    [InlineData("taskbar-windows-1280", "windows", 1280, 800)]
    [InlineData("taskbar-windows-980", "windows", 980, 640)]
    [InlineData("taskbar-empty-980", "empty", 980, 640)]
    [InlineData("taskbar-start-1280", "start", 1280, 800)]
    [InlineData("taskbar-start-980", "start", 980, 640)]
    [InlineData("taskbar-start-empty-980", "start-empty", 980, 640)]
    [InlineData("taskbar-start-unavailable-980", "start-unavailable", 980, 640)]
    [InlineData("taskbar-crowded-980", "crowded", 980, 640)]
    public void Scene(string name, string scene, int width, int height)
    {
        using UiFixture fixture = new();
        var switcher = scene switch
        {
            "empty" => new AppSwitcherViewModel(),
            "crowded" => Switcher(14, 12),
            _ => Switcher(4, 3)
        };
        IProgramsSource? programs = scene switch
        {
            "start-empty" => new EmptyPrograms(),
            "start-unavailable" => null,
            _ => new ProgramsOverlaySource()
        };
        var window = fixture.Taskbar(switcher, programs, width, height);
        if (scene.StartsWith("start", StringComparison.Ordinal))
        {
            UiFixture.Click(window, window.StartButton);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsStartMenuOpen);
        }

        Assert.Equal(width, window.ClientSize.Width);
        Assert.Equal(height, window.ClientSize.Height);
        AssertInside(window, window.StartButton);
        AssertInside(window, window.TrayScroller);
        VisualBaseline.Verify(window, name);
    }

    [AvaloniaFact]
    public void BackClosesTheStartMenuToStartAndThenDismisses()
    {
        using UiFixture fixture = new();
        var window = fixture.Taskbar(Switcher(2, 1), new ProgramsOverlaySource());
        var dismissed = 0;
        window.DismissRequested += () => dismissed++;
        FakeButtonSource buttons = new();
        using GamepadNavigation navigation = new(buttons, window, window.Back,
            preferredFocus: () => window.DefaultFocusTarget, secondary: window.RequestSecondaryAction,
            focusScope: () => window.NavigationRoot);

        UiFixture.Click(window, window.StartButton);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsStartMenuOpen);
        Assert.Same(window.StartMenu, window.NavigationRoot);
        Assert.True(window.StartMenu.IsKeyboardFocusWithin);

        buttons.Press(GamepadButtons.B);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsStartMenuOpen);
        Assert.True(window.StartButton.IsFocused);
        Assert.Equal(0, dismissed);

        buttons.Press(GamepadButtons.B);
        Assert.Equal(1, dismissed);
    }

    [AvaloniaFact]
    public void EscapeClosesTheStartMenuAndThenDismisses()
    {
        using UiFixture fixture = new();
        var window = fixture.Taskbar(Switcher(2, 1), new ProgramsOverlaySource());
        var dismissed = 0;
        window.DismissRequested += () => dismissed++;
        using GamepadNavigation navigation = new(new FakeButtonSource(), window, window.Back,
            preferredFocus: () => window.DefaultFocusTarget, focusScope: () => window.NavigationRoot);

        window.OpenStartMenu();
        Dispatcher.UIThread.RunJobs();
        UiFixture.Key(window, Key.Escape);
        Assert.False(window.IsStartMenuOpen);
        Assert.Equal(0, dismissed);

        UiFixture.Key(window, Key.Escape);
        Assert.Equal(1, dismissed);
    }

    [AvaloniaFact]
    public void TheStartMenuLaunchesRunnableProgramsAndSendsTheRestToTheirEditor()
    {
        using UiFixture fixture = new();
        var window = fixture.Taskbar(Switcher(1, 0), new ProgramsOverlaySource());
        List<string> chosen = [];
        List<string?> managed = [];
        window.ProgramChosen += chosen.Add;
        window.ManageProgramsRequested += managed.Add;
        UiFixture.Click(window, window.StartButton);
        Dispatcher.UIThread.RunJobs();

        UiFixture.Click(window, UiFixture.Tagged<ProgramActionRow>(window, "taskbar.program:mod-manager"));
        UiFixture.Click(window, UiFixture.Tagged<ProgramActionRow>(window, "taskbar.program:monitor"));
        UiFixture.Click(window, UiFixture.Tagged<ProgramActionRow>(window, "taskbar.program:missing"));
        UiFixture.Click(window, window.ManagePrograms);

        Assert.Equal(["mod-manager", "monitor"], chosen);
        Assert.Equal(["missing", null], managed);
        Assert.Equal("Unavailable · executable missing or changed",
            UiFixture.Tagged<ProgramActionRow>(window, "taskbar.program:missing").Description);
    }

    [AvaloniaFact]
    public void WindowChipsActivateAndCloseAndXClosesTheFocusedWindow()
    {
        using UiFixture fixture = new();
        var switcher = Switcher(3, 1);
        var window = fixture.Taskbar(switcher, null);
        List<AppSwitcherEntry> picked = [];
        List<AppSwitcherEntry> closed = [];
        List<(TrayIconEntry Entry, bool Menu)> tray = [];
        window.WindowPicked += picked.Add;
        window.WindowCloseRequested += closed.Add;
        window.TrayIconActivated += (entry, menu, _) => tray.Add((entry, menu));
        var chips = window.AppTiles.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("app-chip")).ToList();
        var closes = window.AppTiles.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("window-close")).ToList();

        UiFixture.Click(window, chips[1]);
        UiFixture.Click(window, closes[2]);
        Assert.Same(switcher.Entries[1], Assert.Single(picked));
        Assert.Same(switcher.Entries[2], Assert.Single(closed));
        // Steam's own window is never offered a close.
        Assert.False(closes[0].IsVisible);

        window.RequestSecondaryAction(chips[1]);
        Assert.Same(switcher.Entries[1], closed[^1]);

        var trayIcon = window.TrayScroller.GetVisualDescendants().OfType<Button>().First();
        UiFixture.Click(window, trayIcon);
        UiFixture.Click(window, trayIcon, MouseButton.Right);
        Assert.Equal([false, true], tray.Select(item => item.Menu));
    }

    [AvaloniaFact]
    public void ATapOnTheDimmedAreaDismissesAndFocusStartsOnTheActiveWindow()
    {
        using UiFixture fixture = new();
        var switcher = Switcher(3, 0);
        var window = fixture.Taskbar(switcher, null);
        var dismissed = 0;
        window.DismissRequested += () => dismissed++;

        Assert.Equal("Window 2", (window.DefaultFocusTarget as Control)?.DataContext is AppSwitcherEntry entry
            ? entry.Title
            : null);
        window.MouseDown(new Point(400, 100), MouseButton.Left);
        window.MouseUp(new Point(400, 100), MouseButton.Left);
        Assert.Equal(1, dismissed);
    }

    [Theory]
    [InlineData(0, 48, true)]
    [InlineData(20, 60, true)]
    [InlineData(0, 47, false)]
    [InlineData(40, 60, false)]
    [InlineData(0, -80, false)]
    public void OnlyAMostlyDownwardDragDismisses(double dx, double dy, bool dismisses)
    {
        Assert.Equal(dismisses, TaskbarWindow.IsSwipeDown(dx, dy));
    }

    private static void AssertInside(Window window, Control control)
    {
        var origin = control.TranslatePoint(default, window) ?? throw new InvalidOperationException();
        Assert.True(origin.X >= 0 && origin.Y >= 0
                                  && origin.X + control.Bounds.Width <= window.ClientSize.Width + 0.5
                                  && origin.Y + control.Bounds.Height <= window.ClientSize.Height + 0.5,
            $"{control.Name} is clipped at {origin} ({control.Bounds.Size}).");
    }

    /// <summary>Synthetic strip state: Steam first, the window the bar opened over third, and tray icons.</summary>
    private static AppSwitcherViewModel Switcher(int windows, int trayIcons)
    {
        string[] titles =
        [
            "Steam", "Performance monitor", "Window 2", "Mod manager — a long window title that trims",
            "Notepad", "Discord", "File Explorer", "Browser", "Lossless Scaling", "Controller tester",
            "Recorder", "Launcher", "Calculator", "Music"
        ];
        AppSwitcherViewModel switcher = new();
        List<AppWindow> listed = [];
        for (var i = 0; i < windows; i++)
        {
            listed.Add(new AppWindow(100 + i, titles[i % titles.Length], (uint)(1000 + i)) { IsMinimized = i == 3 });
        }

        switcher.Reconcile(listed, 102, window => new AppSwitcherEntry(window.Hwnd, window.Title,
            window.Hwnd == 100, Icon(window.Hwnd)) { ProcessId = window.ProcessId });
        List<TrayIconRecord> icons = [];
        for (var i = 0; i < trayIcons; i++)
        {
            icons.Add(new TrayIconRecord(200 + i, (uint)i, Guid.Empty) { Tip = $"Tray icon {i}", IconImage = Icon(i) });
        }

        switcher.ReconcileTray(icons);
        return switcher;
    }

    /// <summary>A flat coloured square standing in for an application icon.</summary>
    private static WriteableBitmap Icon(nint seed)
    {
        uint[] palette = [0xFF4CC2FF, 0xFFFF9D3D, 0xFFA3DCA3, 0xFFB79CFF, 0xFFFFC66D];
        WriteableBitmap bitmap = new(new PixelSize(32, 32), new Vector(96, 96), PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        var pixels = new int[32 * 32];
        Array.Fill(pixels, unchecked((int)palette[(int)(seed % palette.Length)]));
        Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
        return bitmap;
    }

    private sealed class EmptyPrograms : IProgramsSource
    {
        public string LastResult => "";

        public ProgramState[] ReadState()
        {
            return [];
        }

        public bool IsPinned(string id)
        {
            return false;
        }

        public Task SaveAsync(ProgramDefinition definition, bool approveExecutable)
        {
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string id)
        {
            return Task.CompletedTask;
        }

        public Task RememberWindowAsync(string id, ProgramUiCandidate candidate)
        {
            return Task.CompletedTask;
        }

        public Task ForgetUiAsync(string id)
        {
            return Task.CompletedTask;
        }

        public Task ClearPendingAsync(string id)
        {
            return Task.CompletedTask;
        }

        public Task<string> ActivateAsync(string id, CancellationToken cancellationToken)
        {
            return Task.FromResult("");
        }

        public void ReportResult(string message)
        {
        }

        public event Action? Changed
        {
            add { }
            remove { }
        }
    }
}
