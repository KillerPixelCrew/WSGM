using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace WSGM.Settings.Pages;

/// <summary>
///     The Startup section: the startup apps as cards, the timing and the boot splash. A card's More button (or X)
///     shows its actions in the card, where the controller can reach them; Edit and Add app open nested pages.
/// </summary>
public partial class StartupPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public StartupPage()
    {
        InitializeComponent();
    }

    private SettingsViewModel? Model => DataContext as SettingsViewModel;

    /// <summary>Whether the focused element is on a startup app card, where X shows its actions.</summary>
    /// <param name="focused">The focused element.</param>
    internal static bool IsAppCard(IInputElement? focused)
    {
        return CardOf(focused) is not null;
    }

    private static StartupAppRow? CardOf(IInputElement? focused)
    {
        return focused is Visual { DataContext: StartupAppRow row, IsEffectivelyVisible: true } visual
               && visual.FindAncestorOfType<StartupPage>() is not null
            ? row
            : null;
    }

    /// <summary>Shows or hides the actions of the focused card.</summary>
    /// <param name="focused">The focused element.</param>
    /// <returns>False when the focus is not on a card.</returns>
    internal bool ToggleActions(IInputElement? focused)
    {
        if (CardOf(focused) is not { } row)
        {
            return false;
        }

        if (row.ShowActions)
        {
            CloseActions();
        }
        else
        {
            ShowActions(row);
        }

        return true;
    }

    /// <summary>Hides the open card actions and returns focus to that card's More button.</summary>
    /// <returns>Whether a card had its actions open.</returns>
    internal bool CloseActions()
    {
        if (Model?.StartupApps.FirstOrDefault(static app => app.ShowActions) is not { } row)
        {
            return false;
        }

        row.ShowActions = false;
        FocusIn(row, "more");
        return true;
    }

    /// <summary>Focuses a card's More button once the list has laid it out, as Back from its editor does.</summary>
    /// <param name="row">The card's app.</param>
    internal void FocusApp(StartupAppRow row)
    {
        FocusIn(row, "more");
    }

    /// <summary>Opens the editor for an app, with Back returning to its card.</summary>
    /// <param name="row">The app.</param>
    internal void Edit(StartupAppRow row)
    {
        if (Model is not { } model || TopLevel.GetTopLevel(this) is not SettingsWindow window)
        {
            return;
        }

        row.ShowActions = false;
        model.EditedStartupApp = row;
        window.OpenNested(SettingsWindow.StartupSection, "Edit app", () => FocusApp(row));
    }

    /// <summary>Lets the user pick a program file.</summary>
    /// <param name="page">The page asking, for its window's picker.</param>
    /// <returns>The program's path, or null when the picker was closed.</returns>
    internal static async Task<string?> PickProgramAsync(Control page)
    {
        if (TopLevel.GetTopLevel(page) is not { } topLevel)
        {
            return null;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a program",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Programs") { Patterns = ["*.exe"] }]
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private void ShowActions(StartupAppRow row)
    {
        foreach (var other in Model?.StartupApps ?? [])
        {
            other.ShowActions = false;
        }

        row.ShowActions = true;
        FocusIn(row, "edit");
    }

    private void OnMore(object? sender, RoutedEventArgs e)
    {
        ToggleActions(sender as IInputElement);
    }

    private void OnEdit(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is StartupAppRow row)
        {
            Edit(row);
        }
    }

    /// <summary>Runs after the move command: the moved card is rebuilt, so focus follows it to the same button.</summary>
    private void OnMove(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: StartupAppRow row, Tag: string tag })
        {
            row.ShowActions = true;
            FocusIn(row, tag);
        }
    }

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is StartupAppRow row)
        {
            row.ConfirmRemove = true;
            FocusIn(row, "keep");
        }
    }

    private void OnKeep(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is StartupAppRow row)
        {
            row.ConfirmRemove = false;
            FocusIn(row, "remove");
        }
    }

    private void FocusIn(StartupAppRow row, string tag)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (AppList.ContainerFromItem(row) is not { } card)
            {
                AddApp.Focus(NavigationMethod.Directional);
                return;
            }

            var target = card.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
                tag == "more" ? button.Classes.Contains("settings-more") : Equals(button.Tag, tag));
            target?.Focus(NavigationMethod.Directional);
            card.BringIntoView();
        }, DispatcherPriority.Loaded);
    }
}
