using System;
using Avalonia.Controls;
using Avalonia.Platform;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Owns WSGM's notification icon in the Explorer desktop.</summary>
internal sealed class DesktopTray : IDisposable
{
    private readonly TrayIcon _icon;

    internal DesktopTray(Action open, Action gameMode, Action settings, Action exit)
    {
        NativeMenu menu = [];
        Add(menu, "Open WSGM", open);
        Add(menu, "Enter Game Mode", gameMode);
        Add(menu, "Settings", settings);
        menu.Items.Add(new NativeMenuItemSeparator());
        Add(menu, "Exit WSGM", exit);
        using var stream = AssetLoader.Open(new Uri("avares://WSGM/Assets/wsgm.ico"));
        _icon = new TrayIcon
        {
            Icon = new WindowIcon(stream),
            ToolTipText = "WSGM",
            Menu = menu,
            IsVisible = false
        };
        _icon.Clicked += (_, _) => open();
    }

    public void Dispose()
    {
        _icon.Dispose();
    }

    internal void SetDesktop(bool desktop)
    {
        _icon.IsVisible = desktop;
    }

    private static void Add(NativeMenu menu, string title, Action action)
    {
        NativeMenuItem item = new(title);
        item.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error($"Tray menu '{title}' failed", ex);
            }
        };
        menu.Items.Add(item);
    }
}
