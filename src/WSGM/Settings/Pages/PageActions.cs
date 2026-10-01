using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using WSGM.Core;

namespace WSGM.Settings.Pages;

/// <summary>The one way a Settings page starts asynchronous work from a click handler.</summary>
internal static class PageActions
{
    /// <summary>
    ///     Observes a page action across both its synchronous invocation and
    ///     asynchronous continuation. File-picker, archive and policy failures therefore
    ///     stay visible in Settings instead of escaping an async-void event boundary;
    ///     a cancellation is not a failure.
    /// </summary>
    /// <param name="page">The page whose view model shows the failure.</param>
    /// <param name="action">The work to run.</param>
    /// <param name="operation">Names the work in the log and the status strip.</param>
    internal static void Observe(Control page, Func<Task> action, string operation)
    {
        _ = ObserveAsync(page, action, operation);
    }

    private static async Task ObserveAsync(Control page, Func<Task> action, string operation)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"{operation} failed: {ex.Message}");
            if (page.DataContext is SettingsViewModel viewModel)
            {
                viewModel.StatusText = $"{operation} failed: {ex.Message}";
            }
        }
    }
}
