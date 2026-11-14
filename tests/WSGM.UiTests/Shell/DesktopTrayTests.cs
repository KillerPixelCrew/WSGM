using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using WSGM.Shell;

namespace WSGM.UiTests.Shell;

public sealed class DesktopTrayTests
{
    [AvaloniaFact]
    public void DesktopTrayAttachesToTheApplicationSurvivesModeChangesAndRemovesOnlyItsOwnIcon()
    {
        var application = Application.Current!;
        var original = TrayIcon.GetIcons(application);
        using var other = new TrayIcon { ToolTipText = "Other owner", IsVisible = false };
        var icons = new TrayIcons { other };
        TrayIcon.SetIcons(application, icons);
        try
        {
            using var tray = new DesktopTray(NoAction, NoAction, NoAction, NoAction);
            var icon = Assert.Single(icons, item => !ReferenceEquals(item, other));
            Assert.Same(icons, TrayIcon.GetIcons(application));
            Assert.Equal("WSGM", icon.ToolTipText);
            Assert.False(icon.IsVisible);

            tray.SetDesktop(desktop: true);
            Assert.True(icon.IsVisible);
            tray.SetDesktop(desktop: false);
            Assert.False(icon.IsVisible);
            tray.Reregister();
            Assert.True(icon.IsVisible);
            Assert.Equal(2, icons.Count);

            tray.Dispose();
            tray.Dispose();
            Assert.Same(other, Assert.Single(icons));
        }
        finally
        {
            TrayIcon.SetIcons(application, original);
        }
    }

    [AvaloniaFact]
    public void DesktopTrayCreatesTheApplicationCollectionWhenThereWasNone()
    {
        var application = Application.Current!;
        var original = TrayIcon.GetIcons(application);
        TrayIcon.SetIcons(application, null);
        try
        {
            using var tray = new DesktopTray(NoAction, NoAction, NoAction, NoAction);
            var icons = TrayIcon.GetIcons(application);
            Assert.NotNull(icons);
            Assert.Single(icons);
            tray.Dispose();
            Assert.Empty(icons);
        }
        finally
        {
            TrayIcon.SetIcons(application, original);
        }
    }

    private static void NoAction()
    {
    }
}
