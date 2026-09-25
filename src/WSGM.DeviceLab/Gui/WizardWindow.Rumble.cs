using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using WSGM.DeviceLab.Wizard;
using WSGM.DeviceLab.Capture.Live;

namespace WSGM.DeviceLab.Gui;

// The rumble stage: find which ways of driving the motors the tester can feel, confirm which motor is
// on which side, let the tester mark the weakest rumble they can feel on live sliders, then try short
// pulses at full strength and at that minimum. Every output is bounded and followed by an explicit
// zero, and every opened route is zeroed again when the stage ends, however it ends.
internal sealed partial class WizardWindow
{
    private const string FeltCode = "felt";
    private const string NotFeltCode = "not-felt";

    private static readonly string[] FeltLabels = ["Felt it", "Didn't feel it"];
    private static readonly string[] FeltCodes = [FeltCode, NotFeltCode];

    // Pulse page lengths; every one is within LabRumbleRoutes.LongestPulseMilliseconds.
    private const int MaxRumbleReplays = 2;

    private static readonly int[] RumblePulseLengths = [5, 10, 25, 50, 100, 250, 500];

    private async Task RunRumbleAsync(LabProject project, StackPanel page)
    {
        page.Children.Add(Status("Looking for ways to make the device rumble..."));
        var attempt = await Task.Run(() => project.BeginAttempt(LabStages.Rumble, DateTimeOffset.UtcNow));
        var record = ConfirmedRecord(project);
        var discovery = await Task.Run(() => LabRumbleRoutes.Discover(record));
        RumbleSession session = new(discovery);
        try
        {
            await RumbleFlowAsync(page, session);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            session.Failure = ex.Message;
        }
        finally
        {
            // Runs on success, failure and window close alike: every opened route gets a final zero.
            session.ZeroFailures = await Task.Run(() => session.Close());
            await Task.Run(() => WriteRumbleEvidence(project, attempt, record?.Id, session));
        }

        // A final zero that failed is an unverified cleanup, so the stage fails even if the tester finished.
        var summary = session.Summary();
        var status = session.Failure is null && session.ZeroFailures.Count == 0
            ? LabSegmentStatus.Completed
            : LabSegmentStatus.Failed;
        await Task.Run(() => project.Finish(LabStages.Rumble, status, summary, DateTimeOffset.UtcNow));
        if (status == LabSegmentStatus.Completed)
        {
            Next(LabStages.Rumble);
            return;
        }

        page.Children.Clear();
        page.Children.Add(PageTitle("Rumble"));
        if (session.ZeroFailures.Count > 0)
        {
            page.Children.Add(Warning(
                "The motors could not be switched off at the end. If the device is still buzzing, restart it."));
        }

        if (session.Failure is { } failure)
        {
            page.Children.Add(Warning($"This step stopped: {failure}"));
        }

        page.Children.Add(Status(summary));
        page.Children.Add(Buttons(
            Action("Run again", () => StartStage(LabStages.Rumble)),
            Action("Continue", () => Run(page, () =>
            {
                Next(LabStages.Rumble);
                return Task.CompletedTask;
            }))));
    }

    private async Task RumbleFlowAsync(StackPanel page, RumbleSession session)
    {
        var routes = session.Discovery.Routes;
        page.Children.Clear();
        page.Children.Add(PageTitle("Rumble"));
        if (routes.Count == 0)
        {
            page.Children.Add(Status(
                "This tool found no way to make this device rumble. That is a useful result too, and nothing was changed."));
            await AskAsync(page, "Continue");
            return;
        }

        page.Children.Add(Status(
            $"Hold the device in both hands, the way you play. There are {routes.Count} ways to try. Each one gives a single short buzz."));
        StackPanel results = new() { Spacing = 4 };
        StackPanel area = new() { Spacing = 8 };
        page.Children.Add(results);
        page.Children.Add(area);

        // 1. One buzz per route.
        for (var i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            var felt = await ProbeRouteAsync(area, session, route, $"Way {i + 1} of {routes.Count}");
            results.Children.Add(felt
                ? Status($"Way {i + 1} ({route.Name}): felt.")
                : Muted(
                    $"Way {i + 1} ({route.Name}): {(session.Probes[^1].Error is null ? "not felt" : "did not work")}."));
        }

        area.Children.Clear();
        var working = session.Working();
        if (working.Count == 0)
        {
            area.Children.Add(Status(
                "You did not feel any of them. That is a useful result too; it is saved in the report."));
            await AskAsync(area, "Continue");
            return;
        }

        // 2. Measure the first route that worked, then any other the tester picks.
        await CalibrateRouteAsync(page, session, working[0]);
        while (working.Where(route => session.Calibrations.All(done => done.Route != route.Id)).ToArray() is
               { Length: > 0 } others)
        {
            page.Children.Clear();
            page.Children.Add(PageTitle("Rumble"));
            page.Children.Add(Status("Another way also worked. Do you want to measure it too? It takes a few minutes."));
            var choice = await AskAsync(page, [.. others.Select(route => route.Detail), "No, go on"]);
            if (choice == others.Length)
            {
                break;
            }

            await CalibrateRouteAsync(page, session, others[choice]);
        }
    }

    private async Task<bool> ProbeRouteAsync(StackPanel area, RumbleSession session, LabRumbleRoute route,
        string title)
    {
        area.Children.Clear();
        area.Children.Add(Heading(title));
        area.Children.Add(Muted(route.Detail));
        var line = Status(string.Empty);
        area.Children.Add(line);
        LabRumbleProbe probe = new() { Route = route.Id };
        session.Probes.Add(probe);
        var output = await session.OpenAsync(route);
        if (output is null)
        {
            probe.Error = session.OpenErrors[route.Id];
            return false;
        }

        var answer = await PulseAndAskAsync(area, line, "You should feel one short buzz now.", "Did you feel it?",
            () => Play(output, new LabRumbleFrame(60, 60), 400, "probe"), session, probe.Answers, null, FeltLabels,
            FeltCodes);
        if (answer is null)
        {
            probe.Error = session.LastWriteError;
            return false;
        }

        probe.Felt = answer == FeltCode;
        return probe.Felt.Value;
    }

    private async Task CalibrateRouteAsync(StackPanel page, RumbleSession session, LabRumbleRoute route)
    {
        LabRumbleRouteCalibration calibration = new() { Route = route.Id, Name = route.Name };
        session.Calibrations.Add(calibration);
        var output = await session.OpenAsync(route);
        if (output is null)
        {
            calibration.Stopped = session.OpenErrors[route.Id];
            return;
        }

        if (!await ConfirmSidesAsync(page, session, route, output, calibration))
        {
            calibration.Stopped = session.LastWriteError;
            return;
        }

        await SliderPageAsync(page, session, route, output, calibration);
        await PulsePageAsync(page, session, route, output, calibration);
    }

    // Buzzes each motor channel alone and asks where it was felt, so the later pages can name the
    // physical sides. Returns false when a write failed.
    private async Task<bool> ConfirmSidesAsync(StackPanel page, RumbleSession session, LabRumbleRoute route,
        ILabRumbleOutput output, LabRumbleRouteCalibration calibration)
    {
        page.Children.Clear();
        page.Children.Add(PageTitle("Rumble: left and right"));
        page.Children.Add(Muted(route.Detail));
        page.Children.Add(Status("You will feel two buzzes, one at a time. After each, say where you felt it."));
        StackPanel area = new() { Spacing = 8 };
        page.Children.Add(area);
        string[] labels = ["Left side", "Right side", "Both sides", "Didn't feel it"];
        string[] codes = ["left", "right", "both", NotFeltCode];
        for (var channel = 0; channel < 2; channel++)
        {
            area.Children.Clear();
            area.Children.Add(Heading(channel == 0 ? "Buzz 1 of 2" : "Buzz 2 of 2"));
            var line = Status(string.Empty);
            area.Children.Add(line);
            var frame = channel == 0 ? new LabRumbleFrame(70, 0) : new LabRumbleFrame(0, 70);
            var name = channel == 0 ? "left" : "right";
            var answer = await PulseAndAskAsync(area, line, "Next buzz coming...", "Where did you feel it?",
                () => Play(output, frame, 400, $"side-{name}"), session, calibration.SideAnswers, channel, labels,
                codes);
            if (answer is null)
            {
                return false;
            }

            if (channel == 0)
            {
                calibration.LeftChannelFelt = answer;
            }
            else
            {
                calibration.RightChannelFelt = answer;
            }
        }

        calibration.Swapped = (calibration.LeftChannelFelt, calibration.RightChannelFelt) switch
        {
            ("left", "right") => false,
            ("right", "left") => true,
            _ => null
        };
        var swapped = calibration.Swapped == true;
        calibration.Sides.Add(new LabRumbleSideCalibration { Side = "left", Channel = swapped ? "right" : "left" });
        calibration.Sides.Add(new LabRumbleSideCalibration { Side = "right", Channel = swapped ? "left" : "right" });
        return true;
    }

    // Two live sliders. The stream writes the current values while the page is shown and zeroes the
    // motors when it stops, whether the tester continues, a write fails or the window closes.
    private async Task SliderPageAsync(StackPanel page, RumbleSession session, LabRumbleRoute route,
        ILabRumbleOutput output, LabRumbleRouteCalibration calibration)
    {
        page.Children.Clear();
        page.Children.Add(PageTitle("Rumble: the weakest you can feel"));
        page.Children.Add(Muted(route.Detail));
        page.Children.Add(Status(
            "Each slider makes one side rumble while you move it. For each side, slide it down slowly until you can only just feel it, then press \"Lowest I can feel\". Keep the other slider at 0."));
        if (calibration.Swapped is null)
        {
            page.Children.Add(Muted(
                "The two buzzes before were not clearly left and right, so the sliders follow the motors' own names."));
        }

        page.Children.Add(Muted(
            $"The rumble stops by itself after {LabRumbleStream.IdleStopMilliseconds / 1000} seconds without a change. Move the slider to start it again."));

        LabRumbleStream stream = new(output);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        var problem = Warning(string.Empty);
        List<(LabRumbleSideCalibration Side, Slider Slider)> sliders = [];

        void Push()
        {
            var left = 0;
            var right = 0;
            foreach (var (side, slider) in sliders)
            {
                var value = (int)Math.Round(slider.Value);
                if (side.Channel == "left")
                {
                    left = value;
                }
                else
                {
                    right = value;
                }
            }

            stream.Set(new LabRumbleFrame(left, right));
        }

        foreach (var side in calibration.Sides)
        {
            var slider = AddSliderRow(page, side, Push);
            sliders.Add((side, slider));
        }

        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Children.Add(problem);
        page.Children.Add(Buttons(
            Action("Stop the rumble", () =>
            {
                foreach (var (_, slider) in sliders)
                {
                    slider.Value = 0;
                }
            }),
            Action("Continue", () => done.TrySetResult())));

        var streaming = stream.Run(stop.Token);
        _ = streaming.ContinueWith(_ => OnUi(() =>
        {
            if (stream.Error is { } error)
            {
                problem.Text = $"The rumble stopped: {error}";
            }
        }), TaskScheduler.Default);
        try
        {
            await using (Lifetime.Register(() => done.TrySetCanceled(Lifetime)))
            {
                await done.Task;
            }
        }
        finally
        {
            // Leaving the page stops the stream, which writes the zero before the next page starts.
            await stop.CancelAsync();
            await streaming;
            session.SliderSessions.Add(new LabRumbleSliderSession(route.Id, stream.StartedAt, stream.StoppedAt,
                stream.IdleStops, stream.Error, stream.ZeroFailed));
        }
    }

    private static Slider AddSliderRow(StackPanel page, LabRumbleSideCalibration side, Action changed)
    {
        var label = side.Side == "left" ? "Left side" : "Right side";
        TextBlock caption = new() { Width = 90, VerticalAlignment = VerticalAlignment.Center, Text = label };
        TextBlock shown = new() { Width = 50, VerticalAlignment = VerticalAlignment.Center, Text = "0 %" };
        var marked = Muted(string.Empty);
        marked.VerticalAlignment = VerticalAlignment.Center;
        Slider slider = new()
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Width = 320
        };
        slider.PropertyChanged += (_, args) =>
        {
            if (args.Property == RangeBase.ValueProperty)
            {
                shown.Text = $"{(int)Math.Round(slider.Value)} %";
                changed();
            }
        };
        Button mark = new() { Content = "Lowest I can feel" };
        mark.Click += (_, _) =>
        {
            var value = (int)Math.Round(slider.Value);
            if (value == 0)
            {
                marked.Text = "Move the slider up until you feel it first.";
                return;
            }

            side.Marks.Add(new LabRumbleAnswer(DateTimeOffset.UtcNow, "lowest", value));
            side.MinimumStartIntensityPercent = value;
            marked.Text = $"Marked: {value} %";
        };
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(caption);
        row.Children.Add(slider);
        row.Children.Add(shown);
        row.Children.Add(mark);
        row.Children.Add(marked);
        page.Children.Add(row);
        return slider;
    }

    // Buttons per side at full strength and at the marked minimum, at several lengths. Each press plays
    // one pulse and asks whether it was felt.
    private async Task PulsePageAsync(StackPanel page, RumbleSession session, LabRumbleRoute route,
        ILabRumbleOutput output, LabRumbleRouteCalibration calibration)
    {
        page.Children.Clear();
        page.Children.Add(PageTitle("Rumble: short buzzes"));
        page.Children.Add(Muted(route.Detail));
        page.Children.Add(Status(
            "Press a button to feel one short buzz, then say whether you felt it. Try as many as you like, then press Continue."));
        List<Button> pulseButtons = [];
        StackPanel question = new() { Spacing = 8 };
        var line = Status(string.Empty);
        question.Children.Add(line);
        var pending = Task.CompletedTask;
        Button next = new() { Content = "Continue" };
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void Busy(bool busy)
        {
            foreach (var button in pulseButtons)
            {
                button.IsEnabled = !busy;
            }

            next.IsEnabled = !busy;
        }

        void AddRow(LabRumbleSideCalibration side, string strength, int percent, string caption)
        {
            StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = caption,
                Width = 150,
                VerticalAlignment = VerticalAlignment.Center
            });
            foreach (var milliseconds in RumblePulseLengths)
            {
                Button button = new() { Content = $"{milliseconds} ms" };
                button.Click += (_, _) =>
                {
                    if (!pending.IsCompleted)
                    {
                        return;
                    }

                    LabRumblePulseTrial trial = new()
                    {
                        Strength = strength,
                        Percent = percent,
                        Milliseconds = milliseconds
                    };
                    side.Pulses.Add(trial);
                    pending = TryPulseAsync(question, line, session, output, side, trial, button, Busy,
                        ex => done.TrySetException(ex));
                };
                pulseButtons.Add(button);
                row.Children.Add(button);
            }

            page.Children.Add(row);
        }

        foreach (var side in calibration.Sides)
        {
            page.Children.Add(Heading(side.Side == "left" ? "Left side" : "Right side"));
            AddRow(side, "full", 100, "Full strength");
            if (side.MinimumStartIntensityPercent is { } lowest)
            {
                AddRow(side, "lowest", lowest, $"Your lowest ({lowest} %)");
            }
            else
            {
                page.Children.Add(Muted("No lowest level was marked for this side."));
            }
        }

        page.Children.Add(question);
        next.Click += (_, _) => done.TrySetResult();
        page.Children.Add(Buttons(next));
        try
        {
            await using (Lifetime.Register(() => done.TrySetCanceled(Lifetime)))
            {
                await done.Task;
            }
        }
        finally
        {
            // A pulse still playing ends, and writes its zero, before the stage moves on.
            try
            {
                await pending;
            }
            catch (OperationCanceledException)
            {
                // The window is closing; the pulse wrote its own zero.
            }
        }
    }

    private async Task TryPulseAsync(StackPanel question, TextBlock line, RumbleSession session,
        ILabRumbleOutput output, LabRumbleSideCalibration side, LabRumblePulseTrial trial, Button button,
        Action<bool> busy, Action<Exception> stop)
    {
        busy(true);
        try
        {
            var frame = side.Channel == "left"
                ? new LabRumbleFrame(trial.Percent, 0)
                : new LabRumbleFrame(0, trial.Percent);
            var answer = await PulseAndAskAsync(question, line, "Buzz coming...", "Did you feel it?",
                async () => trial.HeldMilliseconds.Add(await Play(output, frame, trial.Milliseconds,
                    $"pulse-{side.Side}-{trial.Strength}-{trial.Milliseconds}")),
                session, trial.Answers, trial.Milliseconds, FeltLabels, FeltCodes, 400);
            if (answer is null)
            {
                trial.Error = line.Text;
                return;
            }

            trial.Felt = answer == FeltCode;
            button.Content = $"{trial.Milliseconds} ms: {(trial.Felt == true ? "felt" : "not felt")}";
            line.Text = string.Empty;
        }
        catch (RumbleStoppedException ex)
        {
            // The tester said the device kept vibrating: the stage ends here.
            stop(ex);
        }
        finally
        {
            busy(false);
        }
    }

    // Plays, then asks. The tester may ask for the pulse again up to twice. Returns the answer's code, or
    // null when a write failed (the pulse is never retried on its own). "Still vibrating, stop" zeroes
    // every route at once and ends the stage.
    private async Task<string?> PulseAndAskAsync(StackPanel area, TextBlock line, string before, string question,
        Func<Task> play, RumbleSession session, List<LabRumbleAnswer> answers, int? value, string[] labels,
        string[] codes, int pauseMilliseconds = 900)
    {
        var replays = 0;
        while (true)
        {
            line.Text = before;
            await Task.Delay(pauseMilliseconds, Lifetime);
            try
            {
                await play();
            }
            catch (LabRumbleWriteException ex)
            {
                line.Text = $"This did not work: {ex.Message}";
                return null;
            }

            line.Text = question;
            var canReplay = replays < MaxRumbleReplays;
            string[] shown = canReplay
                ? [.. labels, "Play again", "Still vibrating, stop"]
                : [.. labels, "Still vibrating, stop"];
            var index = await RumbleAskAsync(area, shown, Array.IndexOf(codes, FeltCode),
                Array.IndexOf(codes, NotFeltCode));
            if (index == shown.Length - 1)
            {
                answers.Add(new LabRumbleAnswer(DateTimeOffset.UtcNow, "still-vibrating", value));
                line.Text = "Switching every motor off...";
                var failed = await session.ZeroNowAsync();
                session.StillVibrating.Add(new LabRumbleStillVibrating(DateTimeOffset.UtcNow, failed));
                throw new RumbleStoppedException(failed.Count == 0
                    ? "You said the device kept vibrating, so the test stopped and every motor was switched off."
                    : "You said the device kept vibrating. The motors could not all be switched off; restart the device.");
            }

            if (canReplay && index == labels.Length)
            {
                replays++;
                answers.Add(new LabRumbleAnswer(DateTimeOffset.UtcNow, "replay", value));
                continue;
            }

            answers.Add(new LabRumbleAnswer(DateTimeOffset.UtcNow, codes[index], value));
            return codes[index];
        }
    }

    // Like AskAsync, but "Felt it" and "Didn't feel it" can also be answered with A and B on the one
    // connected XInput controller, counted on release.
    private async Task<int> RumbleAskAsync(Panel panel, string[] labels, int felt, int notFelt)
    {
        TaskCompletionSource<int> chosen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var row = Buttons([
            .. labels.Select((label, index) => Action(label, () => chosen.TrySetResult(index)))
        ]);
        DispatcherTimer? timer = null;
        if (felt >= 0 && notFelt >= 0 && LabRumblePad.Available())
        {
            row.Children.Add(Muted("or press A (felt) or B (not felt) on the controller"));
            LabRumblePad pad = new();
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) =>
            {
                switch (pad.Poll())
                {
                    case LabRumblePadAnswer.Felt:
                        chosen.TrySetResult(felt);
                        break;
                    case LabRumblePadAnswer.NotFelt:
                        chosen.TrySetResult(notFelt);
                        break;
                }
            };
            timer.Start();
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
                timer?.Stop();
                panel.Children.Remove(row);
            }
        }
    }

    private Task<double> Play(ILabRumbleOutput output, LabRumbleFrame frame, int milliseconds, string purpose)
    {
        var token = Lifetime;
        return Task.Run(() => LabRumbleRoutes.Pulse(output, frame, milliseconds, purpose, token), token);
    }

    private static void WriteRumbleEvidence(LabProject project, string attempt, string? recordId,
        RumbleSession session)
    {
        project.WriteEvidence(attempt, "rumble-routes", new
        {
            Record = recordId,
            session.Discovery.Routes,
            session.Discovery.Notes,
            session.Discovery.HidEndpoints,
            session.OpenErrors,
            session.Probes
        });
        project.WriteEvidence(attempt, "rumble-calibration", new
        {
            Method =
                "Each motor channel buzzed alone (70 percent, 400 ms) to find its side. Live sliders from 0 to 100 percent; the tester marks the lowest strength they can feel per side (MinimumStartIntensity). Pulses per side at 100 percent and at that minimum, at 10 to 500 ms; the shortest felt at 100 percent is the MinimumPulse.",
            Routes = session.Calibrations
        });
        project.WriteEvidence(attempt, "rumble-manual", new { Sliders = session.SliderSessions });
        project.WriteEvidence(attempt, "rumble-writes", new
        {
            Writes = session.Log.Snapshot(),
            session.StillVibrating,
            session.ZeroFailures,
            session.Failure
        });
    }

    /// <summary>The tester reported the motors kept vibrating; the stage stops.</summary>
    private sealed class RumbleStoppedException(string message) : Exception(message);

    /// <summary>Everything one run of the stage opened, wrote and was told.</summary>
    private sealed class RumbleSession(LabRumbleDiscovery discovery)
    {
        private readonly Dictionary<string, ILabRumbleOutput> _outputs = [];

        public LabRumbleDiscovery Discovery { get; } = discovery;

        public LabRumbleLog Log { get; } = new();

        public Dictionary<string, string> OpenErrors { get; } = [];

        public List<LabRumbleProbe> Probes { get; } = [];

        public List<LabRumbleRouteCalibration> Calibrations { get; } = [];

        public List<LabRumbleSliderSession> SliderSessions { get; } = [];

        public List<LabRumbleStillVibrating> StillVibrating { get; } = [];

        public string? Failure { get; set; }

        public IReadOnlyList<string> ZeroFailures { get; set; } = [];

        public string? LastWriteError => Log.Snapshot().LastOrDefault(write => write.Error is not null)?.Error;

        // Opens a route once and keeps it until the stage ends. A route that failed to open stays failed.
        public async Task<ILabRumbleOutput?> OpenAsync(LabRumbleRoute route)
        {
            if (_outputs.TryGetValue(route.Id, out var open))
            {
                return open;
            }

            if (OpenErrors.ContainsKey(route.Id))
            {
                return null;
            }

            try
            {
                var output = await Task.Run(() => LabRumbleRoutes.Open(route, Log));
                _outputs[route.Id] = output;
                return output;
            }
            catch (InvalidOperationException ex)
            {
                OpenErrors[route.Id] = ex.Message;
                return null;
            }
        }

        public IReadOnlyList<LabRumbleRoute> Working()
        {
            return
            [
                .. Discovery.Routes.Where(route =>
                    Probes.Any(probe => probe.Route == route.Id && probe.Felt == true))
            ];
        }

        // Zeroes every opened route at once, keeping them open. Called on the UI thread.
        public Task<IReadOnlyList<string>> ZeroNowAsync()
        {
            var outputs = _outputs.Values.ToArray();
            return Task.Run(() => LabRumbleRoutes.ZeroAll(outputs, "still-vibrating"));
        }

        // Zeroes and releases every opened route. Called off the UI thread.
        public IReadOnlyList<string> Close()
        {
            var outputs = _outputs.Values.ToArray();
            var failed = LabRumbleRoutes.ZeroAll(outputs);
            foreach (var output in outputs)
            {
                output.Dispose();
            }

            return failed;
        }

        public string Summary()
        {
            return LabRumbleSummary.Describe(Discovery.Routes.Count, [.. Working().Select(route => route.Name)],
                Calibrations);
        }
    }
}
