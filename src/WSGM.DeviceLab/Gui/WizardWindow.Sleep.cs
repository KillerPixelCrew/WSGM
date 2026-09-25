using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using WSGM.DeviceLab.Capture.Live;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Gui;

// The sleep stage. The tester presses the power button; the wizard never puts the device to sleep
// itself. After wake it checks the controller, HID endpoints and sensors come back, that a press
// arrives, and, for a device with a controller init, whether the init survived or has to be re-sent.
// The power button's own events are recorded on the way.
internal sealed partial class WizardWindow
{
    private async Task RunSleepAsync(LabProject project, StackPanel page)
    {
        var capture = await CaptureAsync();
        var record = ConfirmedRecord(project);
        var init = LabControllerInit.For(record);
        var attempt = await Task.Run(() => project.BeginAttempt(LabStages.Sleep, DateTimeOffset.UtcNow));
        page.Children.Clear();
        page.Children.Add(PageTitle("Sleep and wake"));

        // A reversible init is applied for the cycle, so the report shows whether it survives sleep.
        LabInitResult? initBefore = null;
        if (init is { Reversible: true })
        {
            page.Children.Add(Status("Setting up the controller as in the buttons step..."));
            initBefore = await SendCuratedInitAsync(record!.Id);
        }

        try
        {
            await RunSleepCycleAsync(project, page, capture, init, initBefore, attempt);
        }
        finally
        {
            if (init is { Reversible: true })
            {
                var problem = await RecoverControllerInitAsync();
                if (problem is not null)
                {
                    page.Children.Add(Warning(
                        $"The controller could not be put back: {problem} Restart the device to reset it."));
                }
            }
        }

        Next(LabStages.Sleep);
    }

    private async Task RunSleepCycleAsync(
        LabProject project,
        StackPanel page,
        LabInputCapture capture,
        LabControllerInitPlan? init,
        LabInitResult? initBefore,
        string attempt)
    {
        page.Children.Add(Status("Checking what is connected before sleep..."));
        var before = await Task.Run(() => LabSleep.Snapshot(capture.Now));

        page.Children.Clear();
        page.Children.Add(PageTitle("Sleep and wake"));
        page.Children.Add(Status(
            "Press the power button briefly to put the device to sleep. Wait about ten seconds, then press the power button again to wake it. This window continues by itself."));
        var line = Status("Waiting for sleep...");
        page.Children.Add(line);

        TaskCompletionSource<bool> resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> skipped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        double? suspendedAt = null;
        double? resumedAt = null;

        void OnSuspendResume(bool suspending)
        {
            if (suspending)
            {
                suspendedAt = capture.Now;
            }
            else if (suspendedAt is not null)
            {
                resumedAt = capture.Now;
                resumed.TrySetResult(true);
            }
        }

        var row = Buttons(
            Action("The device does not go to sleep", () => skipped.TrySetResult(false)),
            Action("Skip this step", () => skipped.TrySetResult(true)));
        page.Children.Add(row);

        capture.SuspendResume += OnSuspendResume;
        capture.BeginStep($"{LabStages.Sleep}/cycle");
        Task finished;
        await using (Lifetime.Register(() =>
                     {
                         resumed.TrySetCanceled(Lifetime);
                         skipped.TrySetCanceled(Lifetime);
                     }))
        {
            finished = await Task.WhenAny(resumed.Task, skipped.Task, Task.Delay(TimeSpan.FromMinutes(10), Lifetime));
        }

        capture.SuspendResume -= OnSuspendResume;
        page.Children.Remove(row);
        Lifetime.ThrowIfCancellationRequested();
        if (finished != resumed.Task)
        {
            var cycle = capture.EndStep();
            var skippedByTester = finished == skipped.Task && skipped.Task.Result;
            var summary = skippedByTester
                ? "Skipped by the tester."
                : finished == skipped.Task
                    ? "The power button did not put the device to sleep."
                    : "The device did not sleep within ten minutes.";
            await Task.Run(() =>
            {
                project.WriteEvidence(attempt, "sleep", new { Before = before, Cycle = cycle, Outcome = summary });
                project.Finish(LabStages.Sleep, skippedByTester ? LabSegmentStatus.Skipped : LabSegmentStatus.Failed,
                    summary, DateTimeOffset.UtcNow);
            });
            return;
        }

        // Back from sleep: watch for everything to return, for up to 30 seconds.
        line.Text = "Welcome back. Checking the controller and sensors...";
        List<LabSleepSnapshot> after = [];
        IReadOnlyList<string> missing = [];
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            var snapshot = await Task.Run(() => LabSleep.Snapshot(capture.Now));
            after.Add(snapshot);
            missing = LabSleep.Missing(before, snapshot);
            if (missing.Count == 0)
            {
                break;
            }

            line.Text = $"Waiting for: {string.Join(", ", missing)}";
            await Task.Delay(500, Lifetime);
        }

        var cycleRecord = capture.EndStep();
        var backAfterMs = missing.Count == 0 && resumedAt is { } resumeTime
            ? Math.Round(after[^1].Ms - resumeTime, 0)
            : (double?)null;

        // Whether the init survived sleep, and whether re-sending it works.
        int? modeAfterWake = null;
        LabInitResult? resent = null;
        if (init is { Reversible: true } && initBefore is { Sent: true })
        {
            modeAfterWake = await CuratedModeAsync(project.Manifest.Device.RecordId!);
            if (modeAfterWake != LabControllerInit.TestMode(init))
            {
                line.Text = "The controller lost its setup during sleep. Setting it up again...";
                resent = await SendCuratedInitAsync(project.Manifest.Device.RecordId!);
            }
        }

        // A press after wake proves the controller delivers input again, not only that it enumerates.
        var press = await SleepPressAsync(page, capture);

        var text = missing.Count == 0
            ? $"Everything came back {backAfterMs / 1000.0:0.0} s after wake"
            : $"Missing after wake: {string.Join(", ", missing)}";
        text += press.Result.Length > 0 ? "; buttons work" : "; no button press arrived";
        if (modeAfterWake is not null)
        {
            text += resent is null ? "; controller setup survived" : "; controller setup had to be sent again";
        }

        page.Children.Add(Status(text + "."));
        await Task.Run(() =>
        {
            project.WriteEvidence(attempt, "sleep", new
            {
                Before = before,
                SuspendedAtMs = suspendedAt,
                ResumedAtMs = resumedAt,
                After = after,
                Missing = missing,
                BackAfterMs = backAfterMs,
                Cycle = cycleRecord,
                Init = init is null
                    ? null
                    : new { init.Feature, Before = initBefore, ModeAfterWake = modeAfterWake, Resent = resent },
                Press = press
            });
            project.Finish(LabStages.Sleep,
                missing.Count == 0 && press.Result.Length > 0 ? LabSegmentStatus.Completed : LabSegmentStatus.Failed,
                text + ".", DateTimeOffset.UtcNow);
        });
        await AskAsync(page, "Continue");
    }

    private async Task<(string Result, double LatencyMs, LabInputStepRecord Record)> SleepPressAsync(
        StackPanel page,
        LabInputCapture capture)
    {
        page.Children.Clear();
        page.Children.Add(PageTitle("Sleep and wake"));
        page.Children.Add(Status("Now press A, or any button on the controller."));
        TaskCompletionSource<string> pressed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnActivity(LabInputActivity activity)
        {
            if (activity.Source is "xinput" or "wgi" or "raw-input"
                && !activity.Detail.StartsWith("mouse", StringComparison.Ordinal))
            {
                pressed.TrySetResult($"{activity.Source}: {activity.Detail}");
            }
        }

        var started = capture.Now;
        capture.Activity += OnActivity;
        capture.BeginStep($"{LabStages.Sleep}/press");
        var row = Buttons(Action("Nothing happens when I press", () => pressed.TrySetResult(string.Empty)));
        page.Children.Add(row);
        string result;
        try
        {
            await using (Lifetime.Register(() => pressed.TrySetCanceled(Lifetime)))
            {
                result = await pressed.Task;
            }
        }
        finally
        {
            capture.Activity -= OnActivity;
        }

        var latency = Math.Round(capture.Now - started, 0);
        page.Children.Remove(row);
        return (result, latency, capture.EndStep());
    }
}
