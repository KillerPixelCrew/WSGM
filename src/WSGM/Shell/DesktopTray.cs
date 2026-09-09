using System;
using Avalonia.Controls;
using Avalonia.Platform;
using WSGM.Settings;

namespace WSGM.Shell;

/// <summary>Owns WSGM's notification icon in the Explorer desktop.</summary>
internal sealed class DesktopTray : IDisposable
{
    private readonly TrayIcon _icon;
    private SettingsWindow? _settings;

    internal DesktopTray(Action open, Action gameMode, Action exit)
    {
        NativeMenu menu = new();
        Add(menu, "Open WSGM", open);
        Add(menu, "Enter Game Mode", gameMode);
        Add(menu, "Settings", OpenSettings);
        menu.Items.Add(new NativeMenuItemSeparator());
        Add(menu, "Exit WSGM", exit);
        using var stream = AssetLoader.Open(new Uri("avares://WSGM/Assets/wsgm.ico"));
        _icon = new TrayIcon
        {
            Icon = new WindowIcon(stream),
            ToolTipText = "WSGM",
            Menu = menu,
            IsVisible = false,
        };
        _icon.Clicked += (_, _) => open();
    }

    internal void SetDesktop(bool desktop) => _icon.IsVisible = desktop;

    private static void Add(NativeMenu menu, string title, Action action)
    {
        NativeMenuItem item = new(title);
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void OpenSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        _settings.WindowState = WindowState.Normal;
        _settings.Activate();
    }

    public void Dispose()
    {
        _icon.Dispose();
        _settings?.Close();
    }
}
