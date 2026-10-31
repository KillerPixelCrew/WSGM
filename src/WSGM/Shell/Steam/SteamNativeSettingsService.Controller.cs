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
    /// <summary>Adds the Controller page's rumble calibration when a device owner exists.</summary>
    /// <param name="controller">The page's sections, appended in order.</param>
    /// <param name="offer">The read's offered command set.</param>
    private void AddRumbleSection(List<SteamSettingsSection> controller, OfferedRows offer)
    {
        if (coordinator is not null)
        {
            var calibration = coordinator.RumbleCalibration;
            var available = coordinator.CanPreviewRumble;
            List<SteamSettingsRow> rumble = [];
            CalibrationRange("strength", "Overall rumble strength", calibration.StrengthPercent, 100, "%",
                "Global user preference. Zero silences output; 100% preserves the original requested strength.");
            CalibrationRange("floor", "Minimum feelable pulse strength", calibration.MinimumStrengthPercent, 100,
                "%",
                "The minimum nonzero bounded-pulse drive. Continuous rumble is never raised to this floor. Device motor limits remain authoritative.");
            CalibrationRange("duration", "Minimum feelable pulse duration", calibration.MinimumPulseMilliseconds,
                500, " ms",
                "Bounded pulses are stretched to this duration or the device minimum, whichever is longer.");
            offer.Add(rumble, new SteamSettingsRow("rumble.test-floor", SteamSettingsRowKind.Action,
                "Test minimum strength and duration",
                "One bounded pulse at the selected minimum motor drive, without overall gain. Adjust until reliably feelable.",
                Disabled: !available, ButtonLabel: "Test pulse"), (_, token) => Preview(true, token));
            offer.Add(rumble, new SteamSettingsRow("rumble.preview", SteamSettingsRowKind.Action,
                "Preview overall calibration",
                "A 25% reference pulse shaped by overall strength, pulse floor and duration. Maximum preview: 500 ms.",
                Disabled: !available, ButtonLabel: "Preview"), (_, token) => Preview(false, token));
            offer.Add(rumble, new SteamSettingsRow("rumble.stop", SteamSettingsRowKind.Action, "Stop rumble preview",
                ButtonLabel: "Stop"), (_, _) => StopRumblePreviewAsync());
            if (!available)
            {
                rumble.Add(new SteamSettingsRow("rumble.availability", SteamSettingsRowKind.Note,
                    "Preview availability",
                    Text:
                    "Connect a WSGM-managed controller with published motor output. Saved calibration applies when that output becomes available."));
            }

            Section(controller, "Rumble calibration", "rumble", rumble);

            void CalibrationRange(string key, string label, int current, int maximum, string suffix,
                string description)
            {
                offer.Add(rumble, new SteamSettingsRow("rumble." + key, SteamSettingsRowKind.Range, label,
                    description + " Saved on completion of the edit.", Number: current, Minimum: 0,
                    Maximum: maximum, Step: 1, Suffix: suffix), async (value, token) =>
                {
                    await coordinator.SetRumbleCalibrationAsync(key, Integer(value), token).ConfigureAwait(false);
                    return SteamUiCommandResult.Applied;
                });
            }

            async Task<SteamUiCommandResult> Preview(bool testFloor, CancellationToken token)
            {
                return await coordinator.PreviewRumbleAsync(testFloor, token).ConfigureAwait(false)
                    ? SteamUiCommandResult.Applied
                    : new SteamUiCommandResult(false,
                        "A preview is already active or controller output is unavailable.");
            }
        }
    }

    /// <summary>
    ///     Why the controller row is inert, stated as the one cause that can still produce it: the session is running
    ///     without device integration.
    /// </summary>
    internal const string ControllerUnavailableDetail =
        "Controller management is unavailable: this session is running without device integration.";

    /// <summary>What the controller row adds to its status while a running game holds the target it launched with.</summary>
    internal const string RestartToRebind = "Restart the application to rebind.";

    /// <summary>The managed-controller target, in Quick Access and on the Settings Controller page.</summary>
    /// <param name="controller">The Settings page's sections.</param>
    /// <param name="offer">The read's offered command set and Quick Access sections.</param>
    /// <remarks>
    ///     The target is WSGM's own setting, read from controller management through the coordinator, so the row and
    ///     the overlay's controller page move the same stored default through the same method.
    /// </remarks>
    private void AddControllerTarget(List<SteamSettingsSection> controller, OfferedRows offer)
    {
        var state = ReadControllerTarget();
        if (!state.Available || state.Targets.Count == 0)
        {
            return;
        }

        // What the controller reports, or nothing when neither the observed nor the selected target is offered:
        // the dropdown still draws, with no selection, so the user can pick.
        var reported = state.ObservedTarget.Length > 0 ? state.ObservedTarget : state.SelectedTarget;
        var selected = state.Targets.FirstOrDefault(target => target.Id == reported);
        List<SteamSettingsRow> rows = [];
        offer.Quick("controllerTarget", offer.Add(rows, new SteamSettingsRow("controller.target",
                SteamSettingsRowKind.Choice, "Controller", Marked(state.StatusText, state.Accent),
                Text: selected?.Id ?? string.Empty,
                Choices: [.. state.Targets.Select(target => new SteamSettingsChoice(target.Id, target.Label))],
                // One target is still a choice while none is shown as selected.
                Disabled: Busy(state.Progress) || (state.Targets.Count < 2 && selected is not null),
                Accent: state.Accent, Icon: "swap", LabelToken: "#QuickAccess_Tab_Settings_Section_Controller_Title",
                Summary: selected?.Label),
            (value, token) => SetControllerTargetAsync(value.GetString()!, token)));
        Section(controller, "Controller", "controller-target", rows);
    }

    /// <summary>The controller target as controller management reports it now.</summary>
    /// <returns>The projection; without a coordinator, the unavailable one.</returns>
    private ControllerTargetRowState ReadControllerTarget()
    {
        return coordinator is null
            ? new ControllerTargetRowState(false, [], string.Empty, string.Empty, string.Empty,
                ControllerUnavailableDetail)
            : ProjectControllerTarget(
                coordinator.ControllerManagementEnabled,
                coordinator.Controllers.Snapshot(),
                coordinator.HasDevice,
                coordinator.Controllers.SupportedTargets,
                coordinator.ChosenControllerTarget());
    }

    /// <summary>Projects controller state into the row's closed vocabulary.</summary>
    /// <param name="enabled">Whether controller management may run at all.</param>
    /// <param name="status">The manager's current truthful state.</param>
    /// <param name="packageInstalled">Whether a native handheld definition is available.</param>
    /// <param name="supportedTargets">Targets the backend on this machine can create.</param>
    /// <param name="chosen">The stored choice, shown as selected while no target is live.</param>
    /// <returns>The state the row renders.</returns>
    internal static ControllerTargetRowState ProjectControllerTarget(
        bool enabled,
        ControllerManagerStatus status,
        bool packageInstalled,
        IReadOnlyList<ManagedControllerTarget> supportedTargets,
        ManagedControllerTarget? chosen = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(supportedTargets);
        if (!enabled)
        {
            return new ControllerTargetRowState(false, [], string.Empty, string.Empty, string.Empty,
                NativeQamUi.Text(status.Detail));
        }

        // Only what the backend can actually build. Offering a target with no encoder behind it persisted the
        // selection, refused target creation and left controller management unavailable.
        ChoiceRowOption[] targets =
        [
            .. DeviceHostRows.TargetChoices(supportedTargets)
                .Select(option => new ChoiceRowOption(option.Target.ToString(), option.Label))
        ];
        var available = status.State is
            ControllerManagementState.Idle or ControllerManagementState.Active or ControllerManagementState.Native;
        var selected = (status.Target ?? chosen) is { } target ? target.ToString() : string.Empty;

        // Observed is what a target actually exists for right now: the virtual one while Active, the physical pad
        // itself while Native. Reporting the selection as observed would hide a target that never came up.
        var observed = status.State is ControllerManagementState.Active or ControllerManagementState.Native
            ? selected
            : string.Empty;
        var detail = status.Detail;
        if (available && string.IsNullOrWhiteSpace(detail) && !packageInstalled)
        {
            detail =
                "This machine has no supported handheld implementation, so no physical controller is being captured.";
        }

        // A running game holds the target it was launched with, so a change reaches it only on the next launch.
        var statusText = NativeQamUi.Text(detail);
        if (status.ApplicationId is not null)
        {
            statusText = statusText.Length > 0 ? $"{statusText} {RestartToRebind}" : RestartToRebind;
        }

        return new ControllerTargetRowState(available, targets, selected, observed,
            status.State switch
            {
                ControllerManagementState.Active or ControllerManagementState.Native => "completed",
                ControllerManagementState.Faulted => "failed",
                _ => string.Empty
            },
            statusText, status.TargetSource is ProfileSource.Game);
    }

    /// <summary>Maps a target name the row sent back onto the enumeration.</summary>
    /// <param name="target">The name.</param>
    /// <param name="parsed">Receives the parsed target.</param>
    /// <returns>Whether the name named a target.</returns>
    /// <remarks>
    ///     Ordinal and case-sensitive on purpose: the row is sent these names by <see cref="ProjectControllerTarget" />,
    ///     so anything else is a caller defect rather than user input to be forgiving about.
    /// </remarks>
    internal static bool TryParseTarget(string target, out ManagedControllerTarget parsed)
    {
        return Enum.TryParse(target, false, out parsed) && Enum.IsDefined(parsed);
    }

    private async Task<SteamUiCommandResult> SetControllerTargetAsync(string target,
        CancellationToken cancellationToken)
    {
        var state = ReadControllerTarget();
        if (coordinator is null || !state.Available)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SteamUiCommandResult(false, state.StatusText);
        }

        if (!TryParseTarget(target, out var parsed))
        {
            return new SteamUiCommandResult(false, $"'{target}' is not a controller target.");
        }

        var status = await coordinator.SetControllerTargetAsync(parsed, cancellationToken).ConfigureAwait(false);
        // Truthful rather than optimistic: the setting is stored either way, but a manager that could not bring the
        // new target up is not a success the row should show as one.
        var succeeded = status.State is not
            (ControllerManagementState.Faulted or ControllerManagementState.Unavailable);
        return new SteamUiCommandResult(succeeded, succeeded ? null : status.Detail);
    }
}
