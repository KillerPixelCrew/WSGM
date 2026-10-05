using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using WSGM.Core;

namespace WSGM.Settings.Pages;

/// <summary>
///     The About settings page: version, licence, the project links and the people and projects
///     WSGM thanks, all from <see cref="Credits" />. The only code is opening a link in the user's
///     default browser.
/// </summary>
public partial class AboutPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public AboutPage()
    {
        InitializeComponent();
    }

    /// <summary>
    ///     Opens an https link at the user's own integrity level. Settings normally runs in the elevated resident
    ///     process, where a direct shell open fails without an unelevated Explorer or starts an elevated browser,
    ///     so an elevated process hands the link to WSGM's <c>--open-link=</c> one-shot through the de-elevating
    ///     task. An unknown elevation, or a task that was not dispatched, opens it directly as before; a dispatch
    ///     whose outcome is unknown is not opened a second time.
    /// </summary>
    /// <param name="url">The https address.</param>
    /// <param name="isElevated">Whether this process is elevated, or null when that is unknown.</param>
    /// <param name="startUnelevated">Runs the one-shot through the de-elevating task, or null when unavailable.</param>
    /// <param name="openDirectly">Opens the address from this process.</param>
    /// <returns>Completes once the link was handed off.</returns>
    internal static async Task OpenLinkAsync(string url, Func<bool?> isElevated,
        Func<string, ScheduledTaskLaunchDisposition>? startUnelevated, Action<string> openDirectly)
    {
        if (startUnelevated is not null && isElevated() is true)
        {
            // Synchronous with a 30 s budget, so never on the UI thread.
            var disposition = await Task.Run(() => startUnelevated(url));
            if (disposition != ScheduledTaskLaunchDisposition.NotDispatched)
            {
                return;
            }

            Log.Warn("About: the link could not be handed to the user's session; opening it from WSGM.");
        }

        openDirectly(url);
    }

    private void OnOpenLink(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string url || !url.StartsWith("https://", StringComparison.Ordinal))
        {
            return;
        }

        Log.Observe(OpenFromPageAsync(url, DataContext as SettingsViewModel), "About link");
    }

    private static async Task OpenFromPageAsync(string url, SettingsViewModel? viewModel)
    {
        Func<string, ScheduledTaskLaunchDisposition>? startUnelevated = null;
        if (viewModel?.HasStore == true && Environment.ProcessPath is { } exe)
        {
            var context = viewModel.Store.Context;
            startUnelevated = link => UnelevatedLauncher.TryStartViaScheduledTask(context, exe, "--open-link=" + link);
        }

        try
        {
            await OpenLinkAsync(url, ElevationCheck.IsCurrentProcessElevated, startUnelevated,
                static link => Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })?.Dispose());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"About: could not open {url}: {ex.Message}");
            if (viewModel is not null)
            {
                viewModel.StatusText = $"Could not open {url}";
            }
        }
    }
}
