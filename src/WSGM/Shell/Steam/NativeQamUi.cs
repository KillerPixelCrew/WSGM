using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>UI-thread marshalling for services whose backing managers own observable UI state.</summary>
/// <remarks>
///     The radio and audio managers reconcile observable collections the taskbar binds to, so their
///     calls are UI-thread owned while bridge requests arrive off the bridge's own thread.
/// </remarks>
internal static class NativeQamUi
{
    /// <summary>How long a device command from Steam's rows may take.</summary>
    internal static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Normalizes an optional detail into renderable text, whole.</summary>
    /// <param name="value">The detail, which may be null or blank.</param>
    /// <returns>The empty string for nothing to say, otherwise the text as given.</returns>
    internal static string Text(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value;
    }

    /// <summary>The word Steam shows for a command's progress.</summary>
    /// <param name="progress">The progress to describe.</param>
    /// <returns>The word, or empty when idle.</returns>
    internal static string ProgressText(CommandProgress progress)
    {
        return progress switch
        {
            CommandProgress.Pending => "applying",
            CommandProgress.Completed => "completed",
            CommandProgress.Failed => "failed",
            CommandProgress.Uncertain => "uncertain",
            _ => string.Empty
        };
    }

    /// <summary>The bounded status line for a device control.</summary>
    /// <param name="view">The capability view.</param>
    /// <param name="available">Whether the control is usable now.</param>
    /// <param name="unavailable">The text when it is unusable and the device gave no reason.</param>
    /// <param name="outOfRange">The text when the desired value no longer fits the descriptor.</param>
    /// <returns>The status line.</returns>
    internal static string StatusText(DeviceCapabilityView view, bool available, string unavailable, string outOfRange)
    {
        var detail = view.LastResult?.Reason?.Detail
                     ?? view.Projection.State.Reason?.Detail;
        if (!available && string.IsNullOrWhiteSpace(detail))
        {
            detail = unavailable;
        }
        else if (view.Projection.DesiredValueOutOfRange)
        {
            detail = outOfRange;
        }

        return Text(detail);
    }

    /// <summary>The Steam command result for a finished device command.</summary>
    /// <param name="result">The device command result.</param>
    /// <param name="fallback">The failure text when the device gave no reason.</param>
    /// <returns>Success when the value reached the device, else the failure with its reason.</returns>
    internal static SteamUiCommandResult CommandResult(CapabilityCommandResult result, string fallback)
    {
        // Accepted is a native per-application value saved for the running game: its driver applies it.
        var succeeded = result.Outcome.IsApplied() || result.Outcome is CommandOutcome.Accepted;
        return new SteamUiCommandResult(succeeded, succeeded ? null : result.Reason?.Detail ?? fallback);
    }

    /// <summary>Runs one manager call on the UI thread.</summary>
    /// <param name="action">The call to make.</param>
    /// <param name="cancellationToken">Checked before dispatch; the call itself is not cancelled.</param>
    /// <returns>A task completing after it ran.</returns>
    internal static Task RunAsync(Action action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }
}
