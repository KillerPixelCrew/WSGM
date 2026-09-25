using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WSGM.DeviceLab.Capture.Live;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Gui;

// The buttons stage: the known device's controller init (snapshotted and restored), an idle baseline,
// then one control at a time. Every input from every device is recorded for each control and
// attributed afterwards; the tester only says when they are done. Any single control can be redone,
// and a redo runs the same init and liveness check first.
internal sealed partial class WizardWindow
{
    private async Task RunButtonsAsync(LabProject project, StackPanel page)
    {
        var record = ConfirmedRecord(project);
        page.Children.Add(Status("Starting the input recorder..."));
        var capture = await CaptureAsync();
        var attempt = await Task.Run(() => project.BeginAttempt(LabStages.Buttons, DateTimeOffset.UtcNow));
        var controls = LabButtonPlan.For(record).ToList();
        var resumed = controls.Any(control =>
            project.Segment($"{LabStages.Buttons}/{control.Id}").Status is not LabSegmentStatus.NotStarted);

        // 1. The known device's init, so buttons that only report after it are seen.
        var init = LabControllerInit.For(record);
        LabInitResult? initResult = null;
        var restoreMode = false;
        if (init is not null)
        {
            page.Children.Clear();
            page.Children.Add(PageTitle("Buttons"));
            page.Children.Add(Heading("Controller setup"));
            page.Children.Add(Status(init.Description));
            var choice = init.Reversible
                ? await AskAsync(page, "Continue", "Test without it")
                : await AskAsync(page, "Test without it", "Do it anyway") == 1
                    ? 0
                    : 1;
            if (choice == 0)
            {
                var line = Status("Setting up the controller...");
                page.Children.Add(line);
                initResult = await SendCuratedInitAsync(record!.Id);
                restoreMode = init.Reversible && initResult.Sent;
                line.Text = initResult.Sent
                    ? "The controller is set up."
                    : $"The controller could not be set up: {initResult.Problem} The test continues without it.";
                if (!initResult.Sent)
                {
                    await AskAsync(page, "Continue");
                }
            }
        }

        // 1b. Without a curated record, HC's own mode commands for this device, only if the tester opts in.
        ModeCommandRun modes = new(project, attempt, "Buttons");
        try
        {
            await OfferModeCommandsAsync(modes, page, record, LabModeStages.Buttons);
            // 2. Baseline: bytes that change on their own (motion sensors, counters) are learned as noise
            //    so they are not mistaken for a press. They are still recorded.
            page.Children.Clear();
            page.Children.Add(PageTitle("Buttons"));
            page.Children.Add(Status(
                "First, put the device down on a table and do not touch it. This lets the test learn what changes by itself."));
            await AskAsync(page, "Ready");
            var countdown = Status(string.Empty);
            page.Children.Add(countdown);
            capture.BeginStep("buttons/baseline", true);
            await CountdownAsync(countdown, "Hands off...", 4);
            var baseline = capture.EndStep();
            await Task.Run(() => project.WriteEvidence(attempt, "setup", new
            {
                capture.Devices,
                capture.Unavailable,
                Noise = capture.NoiseMap(),
                Baseline = baseline,
                Plan = controls,
                Record = record?.Id,
                Init = init is null ? null : new { init.Feature, init.Reversible, Result = initResult }
            }));

            // 3. One control at a time, unless this is a redo of a saved test, which goes straight to
            //    the list.
            if (resumed)
            {
                page.Children.Clear();
                page.Children.Add(PageTitle("Buttons"));
                page.Children.Add(
                    Status("This test already has button results. Redo only some controls, or all of them?"));
                resumed = await AskAsync(page, "Choose controls to redo", "Do all of them again") == 0;
            }

            if (!resumed)
            {
                for (var i = 0; i < controls.Count; i++)
                {
                    if (!await RunControlAsync(project, page, capture, record, controls[i],
                            $"{i + 1} of {controls.Count}"))
                    {
                        // "Skip the rest": the remaining controls are marked, never counted as measured.
                        var remaining = controls.Skip(i + 1).ToList();
                        await Task.Run(() =>
                        {
                            foreach (var control in remaining)
                            {
                                var id = $"{LabStages.Buttons}/{control.Id}";
                                project.BeginAttempt(id, DateTimeOffset.UtcNow);
                                project.Finish(id, LabSegmentStatus.Skipped, "Skipped with the rest.",
                                    DateTimeOffset.UtcNow);
                            }
                        });
                        break;
                    }
                }

                // Buttons the plan did not know about.
                for (var extra = 1; extra <= 12; extra++)
                {
                    page.Children.Clear();
                    page.Children.Add(PageTitle("Any other buttons?"));
                    page.Children.Add(Status(
                        "Does the device have a button, switch or touch area we have not asked about yet? For example a button next to the screen, on the back, or on the top edge."));
                    TextBox name = new()
                    {
                        PlaceholderText = "What is it called or where is it? For example: small button top left",
                        Width = 520,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    page.Children.Add(name);
                    if (await AskAsync(page, "Yes, test it", "No, that is all") == 1)
                    {
                        break;
                    }

                    var label = string.IsNullOrWhiteSpace(name.Text) ? $"Extra button {extra}" : name.Text.Trim();
                    LabControl control = new($"extra-{extra}", label, $"Press and release: {label}.", true);
                    controls.Add(control);
                    await RunControlAsync(project, page, capture, record, control, "extra");
                }
            }

            // 4. Summary, with a redo for any single control.
            await ButtonSummaryAsync(project, page, capture, record, controls);
            await EndModeCommandsAsync(modes, page, false);
        }
        finally
        {
            capture.SwallowShortcuts = false;
            capture.Detailed = false;
            await EndModeCommandsAsync(modes, page, true);
        }

        // 5. Put the controller back the way it was.
        string? restoreProblem = null;
        if (restoreMode)
        {
            page.Children.Clear();
            page.Children.Add(PageTitle("Buttons"));
            var line = Status("Putting the controller back the way it was...");
            page.Children.Add(line);
            restoreProblem = await RecoverControllerInitAsync();
            if (restoreProblem is not null)
            {
                line.Text = $"The controller could not be put back: {restoreProblem}";
                page.Children.Add(Warning(
                    "Restart the device to reset the controller mode. The next time Device Lab starts it will try again."));
                await AskAsync(page, "Continue");
            }
        }

        var reacted = controls.Count(control =>
            project.Segment($"{LabStages.Buttons}/{control.Id}").Status is LabSegmentStatus.Completed);
        await Task.Run(() =>
        {
            project.WriteEvidence(attempt, "summary", new
            {
                Controls = controls.Select(control => new
                {
                    control.Id,
                    control.Name,
                    control.Known,
                    project.Segment($"{LabStages.Buttons}/{control.Id}").Status,
                    project.Segment($"{LabStages.Buttons}/{control.Id}").Summary
                }),
                capture.Devices,
                Init = init is null ? null : new { init.Feature, Result = initResult, RestoreProblem = restoreProblem }
            });
            project.Finish(LabStages.Buttons,
                restoreProblem is null ? LabSegmentStatus.Completed : LabSegmentStatus.Failed,
                restoreProblem is null
                    ? $"{reacted} of {controls.Count} controls recorded."
                    : $"{reacted} of {controls.Count} controls recorded; the controller mode was not restored.",
                DateTimeOffset.UtcNow);
        });
        Next(LabStages.Buttons);
    }

    private async Task ButtonSummaryAsync(
        LabProject project,
        StackPanel page,
        LabInputCapture capture,
        DeviceKnowledgeRecord? record,
        List<LabControl> controls)
    {
        while (true)
        {
            page.Children.Clear();
            page.Children.Add(PageTitle("Buttons: result"));
            page.Children.Add(Status("Each control and what reacted. Redo any that went wrong."));
            TaskCompletionSource<int> chosen = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Grid grid = new()
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                ColumnSpacing = 12,
                RowSpacing = 4
            };
            for (var i = 0; i < controls.Count; i++)
            {
                var state = project.Segment($"{LabStages.Buttons}/{controls[i].Id}");
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var nameCell = new TextBlock
                    { Text = controls[i].Name, FontWeight = FontWeight.SemiBold };
                var resultCell = Muted(state.Status switch
                {
                    LabSegmentStatus.Skipped => "Not on this device",
                    LabSegmentStatus.Completed => state.Summary ?? "Recorded",
                    _ => "Not recorded"
                });
                var index = i;
                var redo = Action("Redo", () => chosen.TrySetResult(index));
                Grid.SetRow(nameCell, i);
                Grid.SetRow(resultCell, i);
                Grid.SetColumn(resultCell, 1);
                Grid.SetRow(redo, i);
                Grid.SetColumn(redo, 2);
                grid.Children.Add(nameCell);
                grid.Children.Add(resultCell);
                grid.Children.Add(redo);
            }

            page.Children.Add(grid);
            page.Children.Add(Buttons(Action("Continue", () => chosen.TrySetResult(-1))));
            int pick;
            await using (Lifetime.Register(() => chosen.TrySetCanceled(Lifetime)))
            {
                pick = await chosen.Task;
            }

            if (pick < 0)
            {
                return;
            }

            await RunControlAsync(project, page, capture, record, controls[pick], "again");
        }
    }

    // Records one control until the tester says it is done or, once something reacted, the control has
    // been quiet for its window. "Do it again" starts a new attempt and keeps the earlier one. A step
    // where nothing reacted is flagged and offered again, never stored silently. Returns false when the
    // tester chose "Skip the rest".
    private async Task<bool> RunControlAsync(
        LabProject project,
        StackPanel page,
        LabInputCapture capture,
        DeviceKnowledgeRecord? record,
        LabControl control,
        string progress)
    {
        var segment = $"{LabStages.Buttons}/{control.Id}";
        while (true)
        {
            var devices = capture.Devices;
            var alive = Liveness(devices, capture.Unavailable);
            page.Children.Clear();
            page.Children.Add(PageTitle($"Buttons ({progress})"));
            page.Children.Add(Heading(control.Name));
            page.Children.Add(Status(control.Instruction));
            page.Children.Add(Muted(control.QuietMs > 0
                ? "The next step starts by itself once you let go. Everything is recorded, even if nothing appears below."
                : "Then press Next here. Everything is recorded, even if nothing appears below."));
            page.Children.Add(Muted($"Listening on: {string.Join(", ", alive)}"));
            var live = Status("Waiting for input...");
            page.Children.Add(live);
            var directory = await Task.Run(() => project.BeginAttempt(segment, DateTimeOffset.UtcNow));

            ConcurrentDictionary<string, byte> seen = new(StringComparer.Ordinal);
            long lastActivity = -1;
            var byId = devices.ToDictionary(device => device.Id, StringComparer.Ordinal);

            void OnActivity(LabInputActivity activity)
            {
                if (activity.Source == "hook" && activity.Detail.StartsWith("mouse", StringComparison.Ordinal))
                {
                    return;
                }

                var device = activity.Device is not null && byId.TryGetValue(activity.Device, out var known)
                    ? LabInputAnalysis.Describe(known)
                    : activity.Device;
                if (!control.Detailed && device is not null
                                      && (device.StartsWith("mouse", StringComparison.Ordinal)
                                          || device.Contains(" 000D:", StringComparison.Ordinal)))
                {
                    return;
                }

                Interlocked.Exchange(ref lastActivity, (long)capture.Now);
                var key = $"{activity.Source} {device}".Trim();
                if (seen.TryAdd(key, 0) && seen.Count <= 6)
                {
                    var text = $"Seen: {string.Join(", ", seen.Keys)}";
                    OnUi(() => live.Text = text);
                }
            }

            capture.Detailed = control.Detailed;
            capture.SwallowShortcuts = true;
            capture.Activity += OnActivity;
            capture.BeginStep(segment);
            using CancellationTokenSource quiet = new();
            var watcher = control.QuietMs > 0
                ? WatchQuietAsync(capture, () => Interlocked.Read(ref lastActivity), control.QuietMs, quiet)
                : Task.CompletedTask;
            int answer;
            try
            {
                answer = await AskAsync(page, quiet.Token, "Next", "Do it again", "This device does not have it",
                    "Skip the rest");
            }
            finally
            {
                capture.Activity -= OnActivity;
                capture.SwallowShortcuts = false;
                capture.Detailed = false;
                await quiet.CancelAsync();
                await watcher;
            }

            if (answer < 0)
            {
                answer = 0;
            }

            if (answer == 3)
            {
                var skipped = capture.EndStep();
                await Task.Run(() =>
                {
                    project.WriteEvidence(directory, "input", skipped);
                    project.Finish(segment, LabSegmentStatus.Skipped, "Skipped with the rest.", DateTimeOffset.UtcNow);
                });
                return false;
            }

            var step = capture.EndStep();
            var allDevices = capture.Devices;
            var candidates = LabInputAnalysis.Candidates(step, allDevices, record);
            var shown = control.Detailed ? candidates : LabInputAnalysis.WithoutPointer(candidates);
            var nothing = answer == 0 && shown.Count == 0;
            if (nothing)
            {
                page.Children.Add(
                    Warning("Nothing reacted to that. Try it again, or keep it as \"nothing happened\"."));
                if (await AskAsync(page, "Try again", "Keep it") == 0)
                {
                    answer = 1;
                }
            }

            var status = answer switch
            {
                0 => LabSegmentStatus.Completed,
                2 => LabSegmentStatus.Skipped,
                _ => LabSegmentStatus.Failed
            };
            var summary = answer switch
            {
                0 when nothing => "Nothing reacted.",
                0 => LabInputAnalysis.Summary(shown),
                2 => "Not on this device.",
                _ => "Redone."
            };
            await Task.Run(() =>
            {
                project.WriteEvidence(directory, "input", step);
                project.WriteEvidence(directory, "candidates", new
                {
                    Control = control,
                    Answer = answer switch { 0 => "done", 2 => "not-on-device", _ => "redo" },
                    Flagged = nothing,
                    Listening = alive,
                    Candidates = candidates,
                    Keys = LabInputAnalysis.Keys(step),
                    Analog = LabInputAnalysis.Analog(step),
                    Devices = allDevices
                });
                project.Finish(segment, status, summary, DateTimeOffset.UtcNow);
            });
            if (answer != 1)
            {
                return true;
            }
        }
    }

    // Ends the wait once input has arrived and then stayed quiet for the control's window.
    private static async Task WatchQuietAsync(
        LabInputCapture capture,
        Func<long> lastActivity,
        int quietMs,
        CancellationTokenSource quiet)
    {
        try
        {
            while (!quiet.IsCancellationRequested)
            {
                await Task.Delay(100, quiet.Token);
                var last = lastActivity();
                if (last >= 0 && capture.Now - last >= quietMs)
                {
                    await quiet.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ended by the tester's answer.
        }
    }

    // What is alive before a step: controller slots, game controllers, HID collections, and any
    // source that could not start.
    private static IReadOnlyList<string> Liveness(IReadOnlyList<LabInputDevice> devices,
        IReadOnlyList<string> unavailable)
    {
        List<string> alive =
        [
            .. devices.Where(device => device.Kind == "xinput").Select(device => device.Name ?? device.Id),
            .. devices.Where(device => device.Kind == "wgi").Select(device => device.Name ?? device.Id),
            $"{devices.Count(device => device.Kind == "hid")} HID collections",
            $"{devices.Count(device => device.Kind == "keyboard")} keyboards",
            "keyboard and mouse hooks",
            "WMI and power events"
        ];
        if (!devices.Any(device => device.Kind == "xinput"))
        {
            alive.Add("no XInput controller");
        }

        alive.AddRange(unavailable.Select(item => $"unavailable: {item}"));
        return alive;
    }
}
