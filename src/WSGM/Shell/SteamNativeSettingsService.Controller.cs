using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

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
}
