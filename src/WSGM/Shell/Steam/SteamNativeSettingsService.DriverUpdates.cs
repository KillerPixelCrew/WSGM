using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

internal sealed partial class SteamNativeSettingsService
{
    private readonly Lock _driverNotesGate = new();
    private readonly HashSet<string> _driverNotes = new(StringComparer.Ordinal);

    private void LogDisplayState(IReadOnlyList<SteamNativeSettingsPage> pages)
    {
        var sections = pages.FirstOrDefault(page => page.Id == SteamNativeSettingsPageId.Display)?.Sections;
        Log.Change("steam.native-settings.display", "Steam native Settings Display: driverUpdates="
            + (driverUpdates is null ? "null" : "present") + "; sections=["
            + string.Join(", ", sections?.Select(section => $"{section.Id}:{section.Rows.Count}") ?? []) + "].");
    }

    private void AddDriverUpdates(List<SteamSettingsSection> display, OfferedRows offer)
    {
        if (driverUpdates is not { } service)
        {
            return;
        }

        List<SteamSettingsRow> rows = [];
        var states = service.Snapshot();
        var busy = states.Any(state => state.Busy);
        HashSet<string> notes;
        lock (_driverNotesGate)
        {
            notes = new HashSet<string>(_driverNotes, StringComparer.Ordinal);
        }
        offer.Add(rows, new SteamSettingsRow("driver-updates.check", SteamSettingsRowKind.Action,
            "Check for GPU driver updates", "Checks your GPU vendor's official catalog.",
            ButtonLabel: "Check now", Disabled: busy), (_, _) => Result(service.Check()));
        offer.Add(rows, new SteamSettingsRow("driver-updates.automatic", SteamSettingsRowKind.Boolean,
            "Check automatically", "Check daily. Downloads and installation always need your confirmation.", service.Automatic),
            (value, _) =>
            {
                service.SetAutomatic(value.GetBoolean());
                return Task.FromResult(SteamUiCommandResult.Applied);
            });
        offer.Add(rows, new SteamSettingsRow("driver-updates.non-whql", SteamSettingsRowKind.Boolean,
            "Include non-WHQL (beta) drivers", "Include official beta, optional and preview releases. Checks again when changed.", service.IncludeNonWhql),
            (value, _) =>
            {
                service.SetIncludeNonWhql(value.GetBoolean());
                return Task.FromResult(SteamUiCommandResult.Applied);
            });
        if (states.Count == 0)
        {
            rows.Add(new SteamSettingsRow("driver-updates.empty", SteamSettingsRowKind.Note, "No supported GPU",
                Text: service.DiscoveryDetail ?? GpuDriverUpdateState.EmptyText));
        }

        foreach (var state in states)
        {
            var id = state.Adapter.InstanceId;
            rows.Add(new SteamSettingsRow("driver-updates.status:" + id, SteamSettingsRowKind.Note,
                state.Adapter.Name, Text: $"Installed: {state.Adapter.Version} · {state.Adapter.Date?.ToString("d") ?? "Date unknown"}\n"
                    + state.StatusText, Summary: state.Stage + "/" + state.Progress));
            if (state.Package is { } package)
            {
                rows.Add(new SteamSettingsRow("driver-updates.certification:" + id, SteamSettingsRowKind.Note,
                    $"Driver {package.Version}", Text: package.Certification));
                offer.Add(rows, new SteamSettingsRow("driver-updates.notes:" + id, SteamSettingsRowKind.Action,
                    "Release notes and license", ButtonLabel: notes.Contains(id) ? "Hide" : "Show"), (_, _) =>
                {
                    lock (_driverNotesGate)
                    {
                        if (!_driverNotes.Remove(id))
                        {
                            _driverNotes.Add(id);
                        }
                    }

                    return Task.FromResult(SteamUiCommandResult.Applied);
                });
                if (notes.Contains(id))
                {
                    rows.Add(new SteamSettingsRow("driver-updates.release-notes:" + id, SteamSettingsRowKind.Note,
                        $"Driver {package.Version}", Text: package.ReleaseNotes + "\n\nLicense and details: " + package.Details));
                }
            }

            if (state.CanInstall)
            {
                var key = "driver-updates.install:" + id + "/" + state.Package!.Identity;
                var refusal = service.InstallRefusal(state.Adapter);
                offer.Add(null, new SteamSettingsRow(key, SteamSettingsRowKind.Action, "Install",
                    Disabled: busy || refusal is not null), (_, _) => Result(service.Install(id, state.Package!.Identity)));
                rows.Add(new SteamSettingsRow("driver-updates.confirm:" + id, SteamSettingsRowKind.Action,
                    "Download and install", refusal ?? GpuDriverUpdateService.InstallWarning,
                    Disabled: busy || refusal is not null,
                    Actions: [new SteamSettingsAction(key, "Download and install…", Confirm: new SteamSettingsConfirmation(
                        When: true, "Install " + state.Package!.Version + "?",
                        GpuDriverUpdateService.InstallWarning + "\n\n" + state.Package.Details, "Download and install"))]));
            }

            if (state.CanCancel)
            {
                offer.Add(rows, new SteamSettingsRow("driver-updates.cancel:" + id, SteamSettingsRowKind.Action,
                    "Cancel preparation", "Stops before any driver installation starts.", ButtonLabel: "Cancel"), (_, _) =>
                {
                    service.Cancel();
                    return Task.FromResult(SteamUiCommandResult.Applied);
                });
            }
        }

        if (service.History is { } history)
        {
            rows.Add(new SteamSettingsRow("driver-updates.history", SteamSettingsRowKind.Note, "Previous installation",
                Text: $"Previous driver: {history.PreviousVersion}. {history.Result}"));
        }

        rows.Add(new SteamSettingsRow("driver-updates.rollback", SteamSettingsRowKind.Note, "Rolling back",
            Text: GpuDriverUpdateService.Rollback));
        var from = display.Count;
        Section(display, "GPU driver updates", "driver-updates", rows);
        Subpage(display, from, new SteamSettingsLevel("driver-updates", "GPU driver updates",
            DriverUpdateSummary(states, service.Automatic), "download",
            states.Any(state => state.CanInstall) ? "Update available" : null, MoreSettingsGroup));
    }

    /// <summary>The driver-update subpage's summary: the adapters it covers and how checks run.</summary>
    /// <param name="states">The adapters' update states.</param>
    /// <param name="automatic">Whether checks run daily.</param>
    /// <returns>The summary.</returns>
    internal static string DriverUpdateSummary(IReadOnlyList<GpuDriverUpdateState> states, bool automatic)
    {
        var checks = automatic ? "Checks daily" : "Checks on request";
        return states.Count == 0
            ? checks
            : string.Join(" · ", states.Select(state => state.Adapter.Name)) + " · " + checks;
    }

    private static Task<SteamUiCommandResult> Result(bool accepted) => Task.FromResult(new SteamUiCommandResult(accepted,
        accepted ? "Started. Progress is shown on this page." : "The package changed or another driver operation is running. Check again before confirming."));
}
