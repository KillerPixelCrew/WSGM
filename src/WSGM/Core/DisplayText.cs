using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>
///     The words for display results. The display library returns outcome codes and native statuses; what
///     Settings, the overlay, the Game Mode entry warning and the log say about them is decided here.
/// </summary>
internal static class DisplayText
{
    /// <summary>Why a layout breaks a layout rule or could not be planned, as Settings and the log show it.</summary>
    /// <param name="problem">The rule or planning problem.</param>
    /// <param name="target">The display a planning problem concerns.</param>
    /// <returns>The sentence for that problem.</returns>
    internal static string Problem(DisplayLayoutProblem problem, DisplayTargetIdentity? target = null)
    {
        return problem switch
        {
            DisplayLayoutProblem.NoDisplays => "A layout needs at least one display.",
            DisplayLayoutProblem.ResolutionOutOfRange =>
                "Every display needs a resolution between 320x200 and 32768x32768.",
            DisplayLayoutProblem.PrimaryNotAtOrigin => "Exactly one display must sit at 0,0 as the primary display.",
            DisplayLayoutProblem.DuplicateDisplay => "A layout cannot list the same display twice.",
            DisplayLayoutProblem.Overlap => "Displays cannot overlap.",
            DisplayLayoutProblem.ScalingOutOfRange => "Display scaling must be between 100 and 500 percent.",
            DisplayLayoutProblem.Detached => "Every display must touch another one; Windows snaps a detached desktop.",
            DisplayLayoutProblem.NoDisplayPath => $"No display path reaches {RouteName(target)} on this adapter.",
            DisplayLayoutProblem.NoFreeSource => $"No free display source is available for {RouteName(target)}.",
            DisplayLayoutProblem.RollbackCaptureFailed =>
                "The current arrangement could not be captured for rollback; nothing was applied.",
            _ => problem.ToString()
        };
    }

    /// <summary>What happened to a layout, as the Game Mode entry warning and the log show it.</summary>
    /// <param name="result">The layout result.</param>
    /// <returns>One sentence.</returns>
    internal static string Layout(DisplayLayoutResult result)
    {
        if (result.Problem is { } problem)
        {
            return problem switch
            {
                DisplayLayoutProblem.ReadFailed =>
                    "The current display configuration could not be read: " + result.FailureMessage,
                DisplayLayoutProblem.ValidationRejected => string.Create(CultureInfo.InvariantCulture,
                    $"Windows rejected this layout during validation (status {result.NativeStatus})."),
                _ => Problem(problem, result.ProblemTarget)
            };
        }

        return result.Outcome switch
        {
            DisplayLayoutOutcome.Applied => "Layout applied.",
            DisplayLayoutOutcome.AlreadyActive => "The desktop already matches this layout.",
            DisplayLayoutOutcome.TargetsAbsent => "Waiting for " + string.Join(", ", result.Absent.Select(Name)) + ".",
            DisplayLayoutOutcome.Refused => result.RollbackSucceeded
                ? "The layout was not confirmed; the previous arrangement was restored."
                : string.Create(CultureInfo.InvariantCulture,
                    $"The layout was not confirmed and the rollback failed with status {result.RollbackStatus}."),
            _ => result.FailureMessage ?? result.Outcome.ToString()
        };
    }

    /// <summary>The per-display settings a layout could not write, one "display: reason" line each.</summary>
    /// <param name="result">The layout result.</param>
    /// <returns>The warnings in the order they happened.</returns>
    internal static IReadOnlyList<string> Warnings(DisplayLayoutResult result)
    {
        return
        [
            .. result.Warnings.Select(warning => Name(warning.Target) + ": " + (warning.Kind == DisplayOutputWarningKind.Hdr
                ? Hdr(warning.Outcome, warning.NativeStatus)
                : Scaling(warning.Outcome, warning.NativeStatus)))
        ];
    }

    /// <summary>Why an advanced colour (HDR) write did not happen.</summary>
    /// <param name="outcome">The write's outcome.</param>
    /// <param name="status">The native status of a refused write.</param>
    /// <returns>A clause for a log line.</returns>
    internal static string Hdr(DisplaySetOutcome outcome, int status)
    {
        return outcome switch
        {
            DisplaySetOutcome.NotActive => "the display is not active, so its colour state was left alone",
            DisplaySetOutcome.Unreadable => "its colour state could not be read",
            DisplaySetOutcome.Unsupported => "this display does not support HDR",
            DisplaySetOutcome.Refused => string.Create(CultureInfo.InvariantCulture,
                $"Windows refused the HDR change (status {status})"),
            _ => "the HDR change was sent"
        };
    }

    /// <summary>Why a scaling write did not happen.</summary>
    /// <param name="outcome">The write's outcome.</param>
    /// <param name="status">The native status of a refused write.</param>
    /// <returns>A clause for a log line.</returns>
    internal static string Scaling(DisplaySetOutcome outcome, int status)
    {
        return outcome switch
        {
            DisplaySetOutcome.NotActive => "the display is not active, so its scaling was left alone",
            DisplaySetOutcome.Unreadable => "its scaling could not be read",
            DisplaySetOutcome.Unsupported => "that is not a scaling step this display offers",
            DisplaySetOutcome.Refused => string.Create(CultureInfo.InvariantCulture,
                $"Windows refused the scaling change (status {status})"),
            _ => "the scaling change was sent"
        };
    }

    /// <summary>What happened to a display mode change, as the overlay shows it.</summary>
    /// <param name="result">The mode result.</param>
    /// <returns>One sentence.</returns>
    internal static string Mode(DisplayModeResult result)
    {
        return result.Outcome switch
        {
            DisplayModeOutcome.Applied => "Display mode applied.",
            DisplayModeOutcome.Stale =>
                "Display changed or the selected mode was not offered. Refresh and select again.",
            DisplayModeOutcome.Unreadable => "Current display mode is unavailable.",
            DisplayModeOutcome.ValidationRefused => "The display rejected mode validation.",
            DisplayModeOutcome.RouteChanged => "Display route changed before application.",
            DisplayModeOutcome.NotAdvertised => "The selected mode is no longer advertised.",
            _ => result.RollbackSucceeded
                ? "Mode was not confirmed; the original mode was restored."
                : "Mode was not confirmed; display recovery could not be verified."
        };
    }

    /// <summary>A display's name in layout results: its friendly name, else its path, else its EDID ids.</summary>
    private static string Name(DisplayTargetIdentity target)
    {
        return target.FriendlyName.Length != 0 ? target.FriendlyName
            : target.DevicePath.Length != 0 ? target.DevicePath
            : $"{target.EdidManufacturerId}-{target.EdidProductCodeId}-{target.FriendlyName}";
    }

    /// <summary>A display's name in planning problems: its friendly name, else its path, else its target id.</summary>
    private static string RouteName(DisplayTargetIdentity? target)
    {
        return target is null ? "this display"
            : target.FriendlyName.Length != 0 ? target.FriendlyName
            : target.DevicePath.Length != 0 ? target.DevicePath
            : string.Create(CultureInfo.InvariantCulture, $"target {target.TargetId}");
    }
}
