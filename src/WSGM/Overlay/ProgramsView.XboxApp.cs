using System.Globalization;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The Xbox app update offered when an Xbox app too old for Game Mode is added or edited.</summary>
public sealed partial class ProgramsView
{
    /// <summary>Whether adding or opening this program should offer the Xbox app update.</summary>
    private static bool OffersXboxAppUpdate(IProgramsSource service, string appUserModelId)
    {
        return XboxAppExperience.Applies(appUserModelId)
               && service.XboxAppUpdate.Phase is not XboxAppUpdatePhase.NotNeeded;
    }

    /// <summary>Asks whether to update an Xbox app too old for Game Mode; the safe answer keeps it as it is.</summary>
    private void ConfirmXboxAppUpdate(IProgramsSource service)
    {
        var installed = service.XboxAppUpdate.Installed?.ToString() ?? "unknown";
        ShowConfirm(this, "Update the Xbox app?",
        [
            $"Xbox app {installed} stays black in Game Mode. Xbox app {XboxAppUpdatePlan.MinimumVersion} and later "
            + "open their own dashboard, which works there.",
            "WSGM downloads the newer build from Microsoft, about 500 MB. While the regular Store does not offer it "
            + "yet, this is Microsoft's Xbox Insider preview build. A running Xbox app closes while it installs."
        ], "Update", () =>
        {
            service.StartXboxAppUpdate();
            Navigate(RenderXboxAppUpdate);
        }, danger: false);
    }

    private void RenderXboxAppUpdate()
    {
        var body = NewStack("Update the Xbox app");
        if (_service is not { } service)
        {
            body.Children.Add(Caption("Programs are unavailable in this session."));
            SetContent(body);
            return;
        }

        var status = service.XboxAppUpdate;
        var installed = status.Installed?.ToString() ?? "unknown";
        switch (status.Phase)
        {
            case XboxAppUpdatePhase.Needed:
            case XboxAppUpdatePhase.Failed:
                body.Children.Add(Tagged(Caption(
                    $"Xbox app {installed} opens a part of the app that Game Mode cannot show, so its window stays "
                    + $"black. Xbox app {XboxAppUpdatePlan.MinimumVersion} and later open their own dashboard, which "
                    + "works in Game Mode."), "program.xbox-update.reason"));
                body.Children.Add(Caption(
                    "WSGM downloads the newer build from Microsoft, about 500 MB. While the regular Store does not "
                    + "offer it yet, this is Microsoft's Xbox Insider preview build. A running Xbox app closes while "
                    + "it installs."));
                if (status.Phase == XboxAppUpdatePhase.Failed)
                {
                    body.Children.Add(Tagged(Caption("The update stopped: " + status.Message),
                        "program.xbox-update.error"));
                }

                body.Children.Add(Tagged(PrimaryRow(
                    status.Phase == XboxAppUpdatePhase.Failed ? "Try again" : "Update Xbox app",
                    "Download from Microsoft and install", Icons.Restart, service.StartXboxAppUpdate),
                    "program.xbox-update.start"));
                body.Children.Add(Tagged(Row("Not now",
                    "Keep the current version. It still works on the Windows desktop.", Icons.ArrowLeft,
                    () => Back()), "program.xbox-update.later"));
                break;
            case XboxAppUpdatePhase.Checking:
            case XboxAppUpdatePhase.Downloading:
            case XboxAppUpdatePhase.Installing:
                body.Children.Add(new OverlayProgress("program.xbox-update.progress", XboxAppProgress(status, service),
                    "program.xbox-update.cancel"));
                body.Children.Add(Caption("The update continues if you leave this page."));
                body.Children.Add(Tagged(Row("Back to Programs", "", Icons.ArrowLeft, () => Back()),
                    "program.xbox-update.back"));
                break;
            case XboxAppUpdatePhase.Updated:
                body.Children.Add(Tagged(Caption(
                        $"Xbox app {installed} is installed. It opens its own dashboard, which works in Game Mode."),
                    "program.xbox-update.done"));
                body.Children.Add(Tagged(PrimaryRow("Done", "", Icons.ArrowLeft, () => Back()),
                    "program.xbox-update.back"));
                break;
            default:
                body.Children.Add(Tagged(Caption($"Xbox app {installed} already works in Game Mode."),
                    "program.xbox-update.done"));
                body.Children.Add(Tagged(PrimaryRow("Done", "", Icons.ArrowLeft, () => Back()),
                    "program.xbox-update.back"));
                break;
        }

        SetLiveContent(body, status, () => _service?.XboxAppUpdate,
            (a, b) => a.Phase == b.Phase && a.Message == b.Message && a.Installed == b.Installed,
            current => XboxAppProgress(current, service));
    }

    private static OverlayProgressState? XboxAppProgress(XboxAppUpdateStatus status, IProgramsSource service)
    {
        var cancel = new OverlayProgressCancel("Cancel update", "Stops the download. The Xbox app is not changed.",
            service.CancelXboxAppUpdate);
        return status.Phase switch
        {
            XboxAppUpdatePhase.Checking => new OverlayProgressState("Asking Microsoft for the newest Xbox app…", true,
                null, cancel),
            XboxAppUpdatePhase.Downloading => new OverlayProgressState(
                $"Downloading Xbox app {status.Target} · {Megabytes(status.Done)} of {Megabytes(status.Total)}", true,
                status.Total > 0 ? (double)status.Done / status.Total : null, cancel),
            XboxAppUpdatePhase.Installing => new OverlayProgressState(
                $"Installing Xbox app {status.Target} · {status.Done} %", true, status.Done / 100d),
            _ => null
        };
    }

    private static string Megabytes(long bytes)
    {
        return (bytes / (1024d * 1024d)).ToString("N0", CultureInfo.CurrentCulture) + " MB";
    }
}
