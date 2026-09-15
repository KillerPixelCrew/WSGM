using System.Text.Json;

namespace WSGM.AllyXLab;

/// <summary>The guided input section. It listens on every source at once, and a press itself is what
/// advances the step: there is no Ready prompt and no repetition.</summary>
internal sealed partial class MainForm
{
    private TaskCompletionSource<string>? _stepAnswer;

    private void StepButtons(params (string Id, string Text)[] options)
    {
        _buttons.Controls.Clear();
        AcceptButton = null;
        foreach ((string id, string text) in options)
        {
            _buttons.Controls.Add(Button(text, () =>
            {
                _stepAnswer?.TrySetResult(id);
                return Task.CompletedTask;
            }));
        }
    }

    private async Task RunInputSectionAsync()
    {
        List<HidEndpoint> endpoints = Hid.Enumerate();
        SessionLog log = new(250_000);
        log.Add("identity", Identity.Read());
        log.Add("endpoints", endpoints.Select(e => e.Public).ToArray());
        var request = new Request(ActionKind.Capture, "Input section", Seconds: 1);
        string outcome = "captured";
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        double lastActivity = 0;
        string first = "";
        int detections = 0;

        using (InputSources sources = new(log, endpoints))
        {
            sources.Activity += activity =>
            {
                seen.Add(activity.Source);
                lastActivity = log.Now;
                if (detections++ == 0)
                {
                    first = activity.Source + ": " + activity.Detail;
                }
            };

            IReadOnlyList<InputStep> steps = InputSteps.All();
            var plan = new List<InputStep>
            {
                new("Channel check", "Press any button on the device so we can see which channels report it.", InputStepKind.Press),
            };
            plan.AddRange(steps);
            for (int index = 0; index < plan.Count; index++)
            {
                CheckStop();
                InputStep step = plan[index];
                _chapter.Text = $"2 / 7 · Input · {index + 1} / {plan.Count}";
                _progress.Value = Math.Min(100, (index + 1) * 100 / plan.Count);
                string answer = await RunStepAsync(step, log, sources, () => (seen.ToArray(), lastActivity, first, detections),
                    () => { seen.Clear(); lastActivity = 0; first = ""; detections = 0; });
                if (answer == "stop")
                {
                    _stopping = true;
                    outcome = "stopped-by-tester";
                    break;
                }

                if (answer == "section")
                {
                    outcome = "section-skipped";
                    break;
                }
            }
        }

        _session.RecordLocal(new Result(request, outcome, "not-needed", log.Events, null));
        _lastResult = "Input section saved.";
        CheckStop();
    }

    private async Task<string> RunStepAsync(
        InputStep step,
        SessionLog log,
        InputSources sources,
        Func<(string[] Sources, double LastActivity, string First, int Count)> state,
        Action reset)
    {
        reset();
        int from = log.Events.Count;
        bool motion = step.Motion;
        using Sensors? sensors = motion ? new Sensors(log) : null;
        using var sensorPoll = new System.Windows.Forms.Timer { Interval = 20 };
        if (sensors is not null)
        {
            sensorPoll.Tick += (_, _) => sensors.Poll();
            sensorPoll.Start();
        }

        _title.Text = step.Name;
        _instruction.Text = step.Instruction;
        _note.Visible = false;
        _progress.Style = ProgressBarStyle.Blocks;
        StepButtons(("nothing", "Nothing happened"), ("repeat", "Do it again"), ("section", "Skip the rest"), ("stop", "Stop and save"));
        log.Add("step-begin", new { Step = step.Name, Kind = step.Kind.ToString(), step.Seconds });
        if (step.Kind == InputStepKind.Baseline)
        {
            sources.Baseline(true);
        }

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stepAnswer = completion;
        double started = log.Now;
        string result = "detected";
        try
        {
            while (true)
            {
                if (completion.Task.IsCompleted)
                {
                    result = await completion.Task;
                    break;
                }

                var (found, last, first, count) = state();
                double elapsed = log.Now - started;
                if (step.Kind is InputStepKind.Baseline or InputStepKind.Hold)
                {
                    int remaining = (int)Math.Ceiling((step.Seconds * 1000 - elapsed) / 1000);
                    _status.Text = $"Hold still… {Math.Max(0, remaining)} s";
                    if (elapsed >= step.Seconds * 1000)
                    {
                        result = "held";
                        break;
                    }
                }
                else
                {
                    _status.Text = count == 0
                        ? "Listening on every source…"
                        : $"Got it · {first} · {found.Length} source(s): {string.Join(", ", found)}";
                    if (count > 0 && log.Now - last >= InputSteps.QuietMilliseconds(step))
                    {
                        break;
                    }
                }

                await Task.Delay(40);
                CheckStop();
            }
        }
        finally
        {
            _stepAnswer = null;
            sensorPoll.Stop();
            if (step.Kind == InputStepKind.Baseline)
            {
                sources.Baseline(false);
            }

            sensors?.Summarize(step.Name);
        }

        var (sourcesSeen, _, firstSource, detections) = state();
        log.Add("step-summary", Summarize(step, log, from, result, sourcesSeen, firstSource, detections));
        if (result == "repeat")
        {
            return await RunStepAsync(step, log, sources, state, reset);
        }

        return result;
    }

    private static object Summarize(InputStep step, SessionLog log, int from, string result, string[] sources, string first, int detections)
    {
        List<LabEvent> window = log.Events.Skip(from).ToList();
        object[] hid = [.. window.Where(e => e.Kind == "raw-hid").Select(e => JsonSerializer.SerializeToElement(e.Data, SessionLog.Json))
            .Where(e => e.GetProperty("ChangedBytes").GetArrayLength() > 0)
            .Select(e => new
            {
                Device = e.GetProperty("Device").GetString(),
                Vid = e.GetProperty("Vid").GetInt32(),
                Pid = e.GetProperty("Pid").GetInt32(),
                ReportId = e.GetProperty("ReportId").GetInt32(),
                Bytes = e.GetProperty("ChangedBytes").EnumerateArray().Select(b => b.GetInt32()).ToArray(),
            })];
        int[] keys = [.. window.Where(e => e.Kind == "raw-keyboard").Select(e => JsonSerializer.SerializeToElement(e.Data, SessionLog.Json).GetProperty("VirtualKey").GetInt32()).Distinct()];
        return new
        {
            Step = step.Name,
            Kind = step.Kind.ToString(),
            Outcome = result,
            Sources = sources,
            FirstSource = first,
            Detections = detections,
            KeyCodes = keys,
            HidCandidates = hid,
            XInputEvents = window.Count(e => e.Kind == "xinput"),
            GameControllerEvents = window.Count(e => e.Kind == "game-controller-reading"),
            AppCommands = window.Count(e => e.Kind == "app-command"),
            FirmwareEvents = window.Count(e => e.Kind == "wmi-event"),
            Note = "Sources are what reported during this step, not proof of which device the press came from.",
        };
    }
}
