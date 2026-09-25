using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using WSGM.DeviceLab.Capture.Live;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Gui;

// The motion stage: every motion source records at once while the tester rests the device, holds it
// in six positions and turns it about each axis. Each step is its own nested segment, so a redo is a
// new attempt and never overwrites evidence. The analysis runs once at the end.
internal sealed partial class WizardWindow
{
    private async Task RunMotionAsync(LabProject project, StackPanel page)
    {
        var record = ConfirmedRecord(project);
        page.Children.Add(Status("Looking for motion sensors..."));
        var attempt = await Task.Run(() => project.BeginAttempt(LabStages.Motion, DateTimeOffset.UtcNow));
        LabMotionRecorder? recorder = null;
        var skipped = false;
        ModeCommandRun modes = new(project, attempt, "Motion sensors");
        try
        {
            recorder = await Task.Run(() => LabMotionRecorder.StartAsync(record, Lifetime));
            LabInputCapture? capture = null;
            string? captureProblem = null;
            try
            {
                capture = await CaptureAsync();
            }
            catch (InvalidOperationException ex)
            {
                captureProblem = ex.Message;
            }

            var inventory = recorder.Inventory;
            var sampled = recorder.Sampled;
            await Task.Run(() => project.WriteEvidence(attempt, "motion-sources", new
            {
                Inventory = inventory,
                InputDevices = capture?.Devices,
                CaptureUnavailable = capture?.Unavailable,
                CaptureProblem = captureProblem,
                KnownRecord = record?.Id,
                KnownMotion = record?.Motion
            }));

            page.Children.Clear();
            page.Children.Add(PageTitle("Motion sensors"));
            page.Children.Add(Status(sampled.Count > 0
                ? $"Found {MotionPlural(sampled.Count, "motion sensor")}."
                : "Windows reports no motion sensor. The steps still check the controller for one."));
            if (captureProblem is not null)
            {
                page.Children.Add(Warning($"Controller reports cannot be recorded: {captureProblem}"));
            }

            page.Children.Add(Status(
                "You will put the device in a few positions and turn it. Each step counts down first so you can get ready. It takes about three minutes."));

            // Takes kept in an earlier run let the tester redo a single step and keep the rest.
            var steps = LabMotionSteps.All;
            var earlier = await Task.Run(() => steps.Select(step => LabMotionSteps.LoadKept(project, step)).ToList());
            string[] choices = earlier.Any(item => item is not null)
                ? ["Do every step", "Redo one step", "Skip this step"]
                : ["Begin", "Skip this step"];
            var choice = await AskAsync(page, choices);
            if (choices[choice] == "Skip this step")
            {
                await Task.Run(() => project.Finish(LabStages.Motion, LabSegmentStatus.Skipped,
                    "Skipped by the tester.",
                    DateTimeOffset.UtcNow));
                skipped = true;
                return;
            }

            // Without a curated record, HC's IMU enables for this controller, only if the tester opts in.
            await OfferModeCommandsAsync(modes, page, record, LabModeStages.Motion);
            List<LabMotionStepRecord> kept = [];
            if (choices[choice] == "Redo one step")
            {
                page.Children.Add(Status("Which step?"));
                var redo = await AskWrappedAsync(page, [.. steps.Select(step => step.Title)]);
                for (var i = 0; i < steps.Count; i++)
                {
                    if (i == redo)
                    {
                        kept.Add(await RunMotionStepAsync(project, page, steps[i], "One step", recorder, capture,
                            RestOf(earlier)));
                    }
                    else if (earlier[i] is { } take)
                    {
                        kept.Add(take);
                    }
                }
            }
            else
            {
                for (var i = 0; i < steps.Count; i++)
                {
                    kept.Add(await RunMotionStepAsync(project, page, steps[i], $"Step {i + 1} of {steps.Count}",
                        recorder, capture, RestOf(kept)));
                }
            }

            await EndModeCommandsAsync(modes, page, false);
            page.Children.Clear();
            page.Children.Add(PageTitle("Motion sensors"));
            page.Children.Add(Status("Working out the results..."));
            var devices = capture?.Devices ?? [];
            var all = recorder.Inventory;
            var summary = await Task.Run(() =>
                LabMotionAnalysis.Analyze(all.Sensors, kept, devices, record, all.HidSensors.Count));
            await Task.Run(() =>
            {
                project.WriteEvidence(attempt, "motion-summary", summary);
                project.Finish(LabStages.Motion, LabSegmentStatus.Completed, summary.Summary, DateTimeOffset.UtcNow);
            });
            ShowMotionResult(page, summary, record);
        }
        finally
        {
            await EndModeCommandsAsync(modes, page, true);
            if (recorder is not null)
            {
                await Task.Run(recorder.Dispose);
            }
        }

        if (skipped)
        {
            Next(LabStages.Motion);
        }
    }

    private void ShowMotionResult(StackPanel page, LabMotionSummary summary, DeviceKnowledgeRecord? record)
    {
        page.Children.Clear();
        page.Children.Add(PageTitle("Motion sensors"));
        page.Children.Add(Status("Done. " + MotionSentence(summary.Summary)));
        if (summary.EmptySteps.Count > 0)
        {
            page.Children.Add(Warning(
                $"Nothing sent any data in {MotionPlural(summary.EmptySteps.Count, "step")}. That is fine if this device has no motion sensor."));
        }

        if (summary.Disagreements.Count > 0)
        {
            page.Children.Add(Status(
                $"{MotionPlural(summary.Disagreements.Count, "difference")} from what is known about {record?.DisplayName ?? "this device"} went into the report."));
        }

        page.Children.Add(Buttons(
            Action("Run again", () => StartStage(LabStages.Motion)),
            Action("Continue", () => Run(page, () =>
            {
                Next(LabStages.Motion);
                return Task.CompletedTask;
            }))));
    }

    // One step: a countdown that doubles as the check of which sources are alive, the recording, a
    // one-line result, then continue or redo. Every take is its own attempt of the nested segment.
    private async Task<LabMotionStepRecord> RunMotionStepAsync(
        LabProject project,
        StackPanel page,
        LabMotionStep step,
        string progress,
        LabMotionRecorder recorder,
        LabInputCapture? capture,
        IReadOnlyList<LabMotionSensorStep>? rest)
    {
        var segment = LabMotionSteps.Segment(step);
        var devices = capture?.Devices ?? [];
        while (true)
        {
            page.Children.Clear();
            page.Children.Add(PageTitle("Motion sensors"));
            page.Children.Add(Muted(progress));
            page.Children.Add(Heading(step.Title));
            page.Children.Add(Status(step.Instruction));
            TextBlock line = new() { FontSize = 28, FontWeight = FontWeight.SemiBold };
            page.Children.Add(line);
            var live = Muted(string.Empty);
            page.Children.Add(live);

            recorder.BeginStep();
            capture?.BeginStep($"motion-{step.Id}-check");
            await CountdownAsync(line, "Starting in", step.CountdownSeconds);
            var (checkInput, checkSensors) = await Task.Run(() => (capture?.EndStep(), recorder.EndStep()));
            var aliveBefore = LabMotionAnalysis.Alive(checkSensors, checkInput, recorder.Sampled, devices);
            live.Text = LabMotionAnalysis.DescribeAlive(aliveBefore);

            var stepAttempt = await Task.Run(() => project.BeginAttempt(segment, DateTimeOffset.UtcNow));
            var started = DateTimeOffset.UtcNow;
            recorder.BeginStep();
            capture?.BeginStep($"motion-{step.Id}");
            var (endedBy, movementStarted, movementEnded) = step.Kind == LabMotionStepKind.Rotation
                ? await RecordMovementAsync(line, step, recorder, rest ?? checkSensors)
                : await HoldAsync(line, step.Seconds);
            var (input, sensors) = await Task.Run(() => (capture?.EndStep(), recorder.EndStep()));
            var alive = LabMotionAnalysis.Alive(sensors, input, recorder.Sampled, devices);
            var result = new LabMotionStepRecord
            {
                Step = step.Id,
                Kind = step.Kind,
                StartedAt = started,
                Seconds = step.Seconds,
                Sensors = sensors,
                Input = input,
                AliveBefore = aliveBefore,
                Alive = alive,
                Empty = alive.Count == 0,
                EndedBy = endedBy,
                MovementStartedMs = movementStarted,
                MovementEndedMs = movementEnded
            };
            line.Text = "Done.";
            var text = LabMotionAnalysis.StepResult(step, result, recorder.Sampled);
            page.Children.Add(result.Empty ? Warning(text) : Status(text));
            var choice = await AskAsync(page, "Continue", "Do it again", "It slipped, do it again");
            result = result with
            {
                Outcome = choice switch
                {
                    0 => "kept",
                    1 => "redo",
                    _ => "slipped"
                }
            };
            var status = choice == 0 && !result.Empty ? LabSegmentStatus.Completed : LabSegmentStatus.Failed;
            var summary = choice switch
            {
                0 => text,
                1 => "Done again at the tester's request.",
                _ => "The device slipped; done again."
            };
            await Task.Run(() =>
            {
                project.WriteEvidence(stepAttempt, $"motion-{step.Id}", result);
                project.Finish(segment, status, summary, DateTimeOffset.UtcNow);
            });
            if (choice == 0)
            {
                return result;
            }
        }
    }

    // The kept rest step's readings: its gyro means are the zero that movement is measured from.
    private static IReadOnlyList<LabMotionSensorStep>? RestOf(IEnumerable<LabMotionStepRecord?> steps)
    {
        return steps.FirstOrDefault(item => item?.Step == LabMotionSteps.Rest && !item.Empty)?.Sensors;
    }

    private async Task<(string EndedBy, double? Started, double? Ended)> HoldAsync(TextBlock line, int seconds)
    {
        await CountdownAsync(line, "Hold still", seconds);
        return ("time", null, null);
    }

    // AllyXLab's movement step: recording runs from the end of the countdown, the movement starts it and
    // 1.2 s of stillness ends it. Without a gyro or accelerometer to follow, it records for a fixed time.
    private async Task<(string EndedBy, double? Started, double? Ended)> RecordMovementAsync(
        TextBlock line,
        LabMotionStep step,
        LabMotionRecorder recorder,
        IReadOnlyList<LabMotionSensorStep> still)
    {
        const double quietMs = 1200;
        const double waitForMovementMs = 10_000;
        const double longestMs = 30_000;
        var clock = Stopwatch.StartNew();
        double? started = null;
        double lastMoving = 0;
        line.Text = "Go ahead.";
        while (true)
        {
            await Task.Delay(40, Lifetime);
            var elapsed = clock.Elapsed.TotalMilliseconds;
            var movement = recorder.Movement(still);
            if (!movement.CanTell)
            {
                if (elapsed >= step.Seconds * 1000)
                {
                    return ("time", null, null);
                }

                line.Text = $"Keep going {Math.Ceiling(step.Seconds - elapsed / 1000)}...";
                continue;
            }

            if (movement.Moving)
            {
                started ??= elapsed;
                lastMoving = elapsed;
                line.Text = "Keep going...";
            }
            else if (started is null && elapsed >= waitForMovementMs)
            {
                return ("no-movement", null, null);
            }

            if (started is not null && elapsed - lastMoving >= quietMs)
            {
                return ("stillness", Math.Round(started.Value), Math.Round(lastMoving));
            }

            if (elapsed >= longestMs)
            {
                return ("time", started is null ? null : Math.Round(started.Value), Math.Round(lastMoving));
            }
        }
    }

    // Like AskAsync, for more choices than fit on one row.
    private async Task<int> AskWrappedAsync(Panel panel, IReadOnlyList<string> labels)
    {
        TaskCompletionSource<int> chosen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WrapPanel row = new() { ItemSpacing = 10, LineSpacing = 10 };
        for (var i = 0; i < labels.Count; i++)
        {
            var index = i;
            row.Children.Add(Action(labels[i], () => chosen.TrySetResult(index)));
        }

        panel.Children.Add(row);
        await using (Lifetime.Register(() => chosen.TrySetCanceled(Lifetime)))
        {
            try
            {
                return await chosen.Task;
            }
            finally
            {
                panel.Children.Remove(row);
            }
        }
    }

    private static string MotionPlural(int count, string noun)
    {
        return count == 1 ? $"1 {noun}" : $"{count} {noun}s";
    }

    private static string MotionSentence(string text)
    {
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..] + ".";
    }
}
