namespace WSGM.AllyXLab;

/// <summary>The rumble section. Every motor route the machine offers is tried with one short pulse,
/// and calibration runs on the first route the tester actually feels.</summary>
internal sealed partial class MainForm
{
    private async Task RunRumbleSectionAsync()
    {
        IReadOnlyList<MotorRoute> routes = Motors.Discover(Hid.Enumerate());
        _session.Observation("Motor routes", routes);
        if (routes.Count == 0)
        {
            await Unavailable("Rumble", "This device offers no motor route: no gamepad output report, no XInput slot and no Windows.Gaming.Input gamepad. Nothing was written.");
            return;
        }

        foreach (MotorRoute route in routes)
        {
            CheckStop();
            Result probe = await Run(
                new(ActionKind.RumbleProbe, "Motor route probe · " + route.Kind, Endpoint: route.Id),
                "Testing a way to reach the motors",
                route.Detail + "\n\nHold the device. It should buzz once, briefly.");
            if (probe.Error is not null)
            {
                _session.Observation("Motor route refused", new { route.Id, route.Kind, probe.Error });
                continue;
            }

            string felt = await Ask("Did it vibrate?", route.Detail + "\n\nAnswer once the device is silent again.",
                ("yes", "Yes, I felt it"), ("no", "No, nothing"), ("stop", "Still vibrating / stop"));
            if (felt == "stop")
            {
                _stopping = true;
                CheckStop();
            }

            _session.ConfirmRecovery("Tester answered after the probe pulse and reported the motors silent.");
            _session.Observation("Motor route answer", new { route.Id, route.Kind, Felt = felt == "yes" });
            if (felt != "yes")
            {
                continue;
            }

            Result result = await Run(
                new(ActionKind.RumbleCalibration, "Guided two-motor calibration", Endpoint: route.Id),
                "Rumble calibration",
                "Answer Felt it or Didn't feel it after each pulse. A and B on the controller work too, when one is visible.");
            if (result.Error is not null || result.Cleanup.Contains("FAILED", StringComparison.Ordinal))
            {
                await Failure(result);
                return;
            }

            // The final zero output must have returned before the checkpoint is cleared.
            if (result.Cleanup.StartsWith("zero-output-sent", StringComparison.Ordinal))
            {
                _session.ConfirmRecovery("Tester confirmed motors silent after each scored calibration pulse.");
            }

            await Ask("Rumble calibration recorded", RumbleSummary(result), ("next", "Continue to lighting"));
            return;
        }

        await Unavailable("Rumble", "No available motor route produced a pulse you could feel. Every attempt and answer is recorded.");
    }
}
