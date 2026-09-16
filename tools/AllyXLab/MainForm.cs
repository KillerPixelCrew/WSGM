using System.Text;
using System.Text.Json;

namespace WSGM.AllyXLab;

internal sealed partial class MainForm : Form
{
    private readonly Session _session = new();
    private readonly Label _chapter = new() { AutoSize = true, Font = new Font("Segoe UI", 11), ForeColor = Color.DimGray };
    private readonly Label _title = new() { AutoSize = true, MaximumSize = new Size(810, 0), Font = new Font("Segoe UI", 22, FontStyle.Bold) };
    private readonly Label _instruction = new() { AutoSize = true, MaximumSize = new Size(810, 0), Font = new Font("Segoe UI", 13) };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(810, 0), ForeColor = Color.DarkSlateBlue };
    private readonly ProgressBar _progress = new() { Width = 790, Height = 14, Style = ProgressBarStyle.Blocks };
    private readonly FlowLayoutPanel _buttons = new() { AutoSize = true, MaximumSize = new Size(810, 0), WrapContents = true };
    private readonly TextBox _note = new() { Width = 790, Height = 58, Multiline = true, MaxLength = 2000, PlaceholderText = "Optional: anything unusual you noticed" };
    private readonly Button _stop = new() { Text = "Stop / save progress", AutoSize = true, Enabled = false };
    private readonly System.Windows.Forms.Timer _controller = new() { Interval = 50 };
    private TaskCompletionSource<string>? _pending;
    private bool _running, _stopping, _feedback, _controllerReleased;
    private string? _rgbEndpoint;
    private string _detail = "Instructions and license notices are included in this executable.";
    private string? _endReason;
    private string _lastResult = "";

    internal MainForm()
    {
        Text = "ROG Ally X Lab · guided test · " + LabVersion.Text;
        Size = new Size(920, 720); MinimumSize = new Size(850, 620);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 11);
        KeyPreview = true;
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(24) };
        foreach (Control control in new Control[] { _chapter, _title, _instruction, _progress, _status, _note, _buttons })
        { control.Margin = new Padding(0, 0, 0, 18); content.Controls.Add(control); }
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12) };
        _stop.Click += (_, _) => Stop();
        var details = new Button { Text = "Details", AutoSize = true };
        details.Click += (_, _) => ShowDetails();
        var folder = new Button { Text = "Capture files", AutoSize = true };
        folder.Click += (_, _) => OpenFolder(_session.DirectoryPath);
        var help = new Button { Text = "Instructions / licenses", AutoSize = true };
        help.Click += (_, _) =>
        {
            var text = new StringBuilder(); var assembly = typeof(MainForm).Assembly;
            foreach (string resource in assembly.GetManifestResourceNames().Order())
            { using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!); text.AppendLine(resource).AppendLine(reader.ReadToEnd()); }
            ShowText("Instructions / licenses", text.ToString());
        };
        footer.Controls.AddRange([_stop, details, folder, help]);
        Controls.Add(content); Controls.Add(footer);
        _chapter.Text = "One guided session";
        _title.Text = "Let’s map your Ally X";
        _instruction.Text = "Close games, Armoury Crate, Handheld Companion, G-Helper and WSGM. Disconnect other controllers. Keep the battery above 30% and the vents clear.\n\nWe’ll guide you through buttons, motion, rumble, lighting, power and fans, then save a ZIP to send back. In the input part there is nothing to confirm: press the control we name and the next one appears by itself. If a control does nothing, press Nothing happened. Expect about 15 minutes.\n\nThe report includes model/BIOS, ASUS input reports, motion data and your answers. No upload happens automatically.";
        _note.Visible = false;
        var start = Button("Start", async () => await StartAsync());
        _buttons.Controls.Add(start); AcceptButton = start;
        _controller.Tick += (_, _) => ControllerAnswer(); _controller.Start();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && _running) { Stop(); e.Handled = true; } };
        FormClosing += (_, e) =>
        {
            if (_running) { e.Cancel = true; Stop(); }
            else if (File.Exists(_session.RecoveryPath) && MessageBox.Show("A recovery record is still pending. Close without confirming restoration?", Text, MessageBoxButtons.YesNo) != DialogResult.Yes)
            {
                e.Cancel = true;
            }
        };
        FormClosed += (_, _) => _controller.Dispose();
    }
    private static Button Button(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(170, 48), Margin = new Padding(0, 0, 12, 10) };
        button.Click += async (_, _) => await action(); return button;
    }
    private async Task<string> Ask(string title, string instruction, params (string Id, string Text)[] options)
    {
        if (_stopping && _running)
        {
            return "stop";
        }

        _title.Text = title; _instruction.Text = instruction; _status.Text = _lastResult;
        _progress.Style = ProgressBarStyle.Blocks;
        _note.Clear(); _note.Visible = true;
        _buttons.Controls.Clear(); AcceptButton = null;
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = completion; _controllerReleased = false;
        foreach (var option in options)
        {
            var button = Button(option.Text, () =>
            {
                if (ReferenceEquals(_pending, completion))
                {
                    Answer(option.Id);
                }

                return Task.CompletedTask;
            });
            _buttons.Controls.Add(button); AcceptButton ??= button;
        }
        try
        {
            string answer = await completion.Task;
            if (answer == "stop" && _running)
            {
                _stopping = true;
            }

            if (_note.Text.Length > 0)
            {
                _session.Observation(title, _note.Text);
            }

            return answer;
        }
        finally
        {
            if (ReferenceEquals(_pending, completion))
            {
                _pending = null;
            }

            _feedback = false;
            foreach (Control button in _buttons.Controls)
            {
                button.Enabled = false;
            }

            AcceptButton = null;
        }
    }
    private void Answer(string answer)
    {
        if (_pending?.TrySetResult(answer) == true)
        {
            foreach (Control button in _buttons.Controls)
            {
                button.Enabled = false;
            }
        }
    }
    private void Stop()
    {
        if (!_running)
        {
            return;
        }

        _stopping = true; _session.Cancel(); Answer("stop");
        _status.Text = "Stopping and restoring. Please wait; don’t unplug the device.";
    }
    private void CheckStop()
    {
        if (_stopping)
        {
            throw new OperationCanceledException();
        }
    }
    private void Chapter(int number, string text)
    { _chapter.Text = $"{number} / 7 · {text}"; _progress.Value = Math.Min(100, (number - 1) * 100 / 7); }
    private async Task StartAsync()
    {
        if (_running)
        {
            return;
        }

        _running = true; _stop.Enabled = true;
        try
        {
            Chapter(1, "Device check");
            if (File.Exists(_session.RecoveryPath))
            {
                await Recovery();
            }

            CheckStop();
            await DeviceCheckAsync();
            CheckStop();
            var inventory = await Run(new(ActionKind.Inventory, "Initial inventory"), "Checking your device", "Reading the model, firmware, controller interfaces and sensors.");
            if (inventory.Error is not null)
            {
                throw new InvalidOperationException(inventory.Error);
            }

            ReadEndpoints(inventory);
            var identity = ((JsonElement)inventory.Events.First(e => e.Kind == "identity").Data).Deserialize<Identity>()!;
            if (!identity.MatchesModel)
            {
                throw new InvalidOperationException("This identity is not yet supported. We saved the inventory; please send it back so we can review it.");
            }

            Chapter(2, "Input");
            await RunInputSectionAsync();

            Chapter(3, "Rumble");
            await RunRumbleSectionAsync();
            CheckStop();
            Chapter(4, "Lighting");
            if (_rgbEndpoint is null)
            {
                await Unavailable("Lighting", "We could not uniquely identify the supported lighting interface. The inventory has been saved for review.");
            }
            else if (await Ask("Prepare the lights", "Set lighting OFF using the OEM controls, then close that manager again. "
                + (identity.Variant == AllyXModel.XboxAllyX ? "Also turn off Dynamic Lighting in Windows Settings under Personalization, so Windows does not repaint the lights during the test. " : "")
                + "The tests will flash colors and return brightness to OFF. The remembered color/mode may change; restore those with OEM controls afterward.", ("ready", "Lights are off — begin"), ("skip", "Skip lighting")) == "ready")
            {
                string[] zones = ["both rings", "left ring outer half", "left ring inner half", "right ring inner half", "right ring outer half"];
                string[] colors = ["red", "green", "blue"];
                for (int zone = 0; zone < zones.Length; zone++)
                {
                    for (int color = 0; color < colors.Length; color++)
                    {
                        CheckStop(); string label = $"{colors[color]} · {zones[zone]}";
                        string ready = await Ask("Lighting: " + label, "This area should light up for two seconds, then go dark again.", ("ready", "Ready — flash color"), ("skip", "Skip this test"));
                        CheckStop();
                        if (ready == "skip") { _session.Observation(label, "Skipped by tester"); continue; }
                        var result = await Run(new(ActionKind.Rgb, label, color, zone, _rgbEndpoint), "Watch the lights", "Expected: " + label);
                        if (result.Error is not null || result.Cleanup.Contains("FAILED", StringComparison.Ordinal)) { await Failure(result); continue; }
                        string answer = await Ask("Did the lighting match?", "Expected: " + label + ". Answer after the lights are off again.", ("correct", "Matched; lights off"), ("different", "Different; lights off"), ("stop", "Still on / stop"));
                        if (answer is "correct" or "different")
                        { _session.Observation(label, new { Matched = answer == "correct", LightsOffConfirmed = true }); _session.ConfirmRecovery("Tester confirmed visible OFF after " + label); }
                        else { _stopping = true; CheckStop(); }
                    }
                }
            }
            else
            {
                _session.Observation("Lighting", _stopping ? "Section stopped" : "Section skipped by tester");
            }

            CheckStop();
            for (int source = 1; source >= 0; source--)
            {
                Chapter(source == 1 ? 5 : 6, source == 1 ? "Power and fans · plugged in" : "Power and fans · battery");
                string ready = await Ask(source == 1 ? "Plug in the charger" : "Unplug the charger", "Keep the battery above 30%. Leave the power source unchanged throughout this section. We’ll test the three firmware profiles, then 13 / 17 / 25 W power limits, then both fans. Each test reads and restores the original settings.", ("ready", "Ready"), ("skip", "Skip this section"));
                CheckStop();
                if (ready == "skip") { _session.Observation("Power source " + source, "Section skipped"); continue; }
                if (!Worker.GetSystemPowerStatus(out var power) || power.Ac != source)
                { await Unavailable("Power source not ready", "Windows did not report the expected power source. This section will be skipped without writing settings."); continue; }
                await Run(new(ActionKind.ReadPower, "Power/fan baseline", ExpectedAc: source), "Reading original settings", "Reading firmware mode, power limits and fan curves.");
                var actions = new List<(Request Request, string Text)>();
                foreach (var profile in new[] { (2, "Silent"), (0, "Performance"), (1, "Turbo") })
                {
                    actions.Add((new(ActionKind.Profile, profile.Item2 + " firmware profile", profile.Item1, ExpectedAc: source), "Select this firmware profile, read the resulting limits and curves, then restore the original settings."));
                }

                foreach (int watts in new[] { 13, 17, 25 })
                {
                    actions.Add((new(ActionKind.Tdp, $"{watts} W power envelope", watts, ExpectedAc: source), $"Set all three power limits to {watts} W, read them back, then restore the original settings."));
                }

                for (int fan = 0; fan < 2; fan++)
                {
                    actions.Add((new(ActionKind.Fan, fan == 0 ? "CPU fan" : "GPU fan", 15, fan, ExpectedAc: source), "Raise this fan’s curve by 15 percentage points without reducing cooling, read back, then restore. Listen for a change."));
                }

                foreach (var action in actions)
                {
                    CheckStop();
                    string next = await Ask(action.Request.Label, action.Text, ("ready", "Ready — run test"), ("skip", "Skip this test"), ("section", "Skip remaining section"));
                    CheckStop();
                    if (next == "section") { _session.Observation("Remaining power section", "Skipped by tester"); break; }
                    if (next == "skip") { _session.Observation(action.Request.Label, "Skipped by tester"); continue; }
                    var result = await Run(action.Request, action.Request.Label, "Applying, reading back and restoring. Keep the power source unchanged.");
                    if (result.Error is not null || result.Cleanup.Contains("FAILED", StringComparison.Ordinal))
                    { await Failure(result); break; }
                    if (action.Request.Action == ActionKind.Fan)
                    {
                        string heard = await Ask("Did you hear the " + action.Request.Label + " change?", "The original curve is restored. Record what you heard; the curve readback is saved separately.", ("yes", "Yes"), ("no", "No / unsure"));
                        CheckStop(); _session.Observation(action.Request.Label + " audible response", heard);
                    }
                }
            }
            CheckStop();
        }
        catch (OperationCanceledException) { _endReason = "Stopped at your request. Completed measurements are saved."; }
        catch (Exception e) { _stopping = true; _endReason = e.Message; _detail = e.ToString(); _session.Observation("Wizard stopped", e.Message); }
        finally
        {
            _running = false; _stop.Enabled = false; _feedback = false;
            await Finish();
        }
    }
    private async Task<Result> Run(Request request, string title, string instruction)
    {
        CheckStop(); _title.Text = title; _instruction.Text = instruction;
        _status.Text = "Preparing…"; _buttons.Controls.Clear(); AcceptButton = null; _note.Visible = false;
        _progress.Style = ProgressBarStyle.Marquee;
        var result = await _session.RunAsync(request, text => _status.Text = text, async prompt =>
        {
            string mode = prompt.GetProperty("Mode").GetString()!;
            _feedback = mode == "feedback";
            var options = mode == "ready"
                ? new List<(string, string)> { ("ready", "Ready — start this phase"), ("stop", "Stop calibration") }
                : new List<(string, string)> { ("felt", "Felt it → next weaker"), ("not-felt", "Didn’t feel it"), ("stop", "Still vibrating / stop") };
            if (mode == "feedback" && prompt.GetProperty("CanRepeat").GetBoolean())
            {
                options.Insert(2, ("repeat", "Replay this pulse"));
            }

            string answer = await Ask(prompt.GetProperty("Title").GetString()!, prompt.GetProperty("Text").GetString()!, options.ToArray());
            if (answer == "stop")
            {
                _stopping = true;
            }

            return answer;
        });
        _lastResult = result.Error is not null ? "Previous step could not complete; details saved."
            : result.Cleanup.Contains("FAILED", StringComparison.Ordinal) ? "Restoration needs attention."
            : result.Outcome == "applied-readback-matched" ? "Readback matched; original settings restored."
            : result.Outcome == "readback-mismatch" ? "Readback differed; original settings restored."
            : request.Action == ActionKind.Capture ? request.Label + ": capture saved."
            : request.Action == ActionKind.RumbleCalibration ? "Rumble calibration recorded."
            : "Results saved.";
        _detail = JsonSerializer.Serialize(result, SessionLog.Json);
        _progress.Style = ProgressBarStyle.Blocks;
        return result;
    }
    private async Task Failure(Result result)
    {
        CheckStop();
        if (File.Exists(_session.RecoveryPath)) { await Recovery(); return; }
        await Ask("This step could not complete", (result.Error ?? result.Cleanup) + "\n\nThe result was saved. We won’t retry this write automatically.", ("next", "Continue"), ("stop", "Finish and save"));
        // Stop choices from ordinary wizard questions have no separate callback.
        if (_stopping)
        {
            CheckStop();
        }
    }
    private async Task Recovery()
    {
        _detail = File.ReadAllText(_session.RecoveryPath);
        string answer = await Ask("Original settings need checking", "A previous action could not confirm restoration. Open Details to see the saved original settings. Restore them using the OEM controls before continuing, including silent motors and the original lighting state.", ("restored", "I restored the originals"), ("stop", "Finish and save"));
        if (answer == "restored")
        {
            _session.ConfirmRecovery("Tester reviewed the recovery record and confirmed manual OEM restoration.");
        }
        else { _stopping = true; CheckStop(); }
    }
    private async Task Unavailable(string title, string text)
    { _session.Observation(title, text); await Ask(title, text, ("next", "Continue")); CheckStop(); }
    private void ReadEndpoints(Result inventory)
    {
        var endpoints = inventory.Events.Where(e => e.Kind == "endpoints").SelectMany(e => ((JsonElement)e.Data).EnumerateArray())
            .Where(e => e.GetProperty("Vid").GetInt32() == 0x0B05 && e.GetProperty("Pid").GetInt32() == 0x1B4C).ToArray();
        _rgbEndpoint = Unique(endpoints.Where(e => e.GetProperty("Page").GetInt32() == 0xFF31 && e.GetProperty("Usage").GetInt32() == 0x80 && e.GetProperty("OutputBytes").GetInt32() >= 64));
    }
    private static string? Unique(IEnumerable<JsonElement> entries)
    { string[] ids = entries.Select(e => e.GetProperty("Id").GetString()!).Distinct().ToArray(); return ids.Length == 1 ? ids[0] : null; }
    private static string RumbleSummary(Result result)
    {
        var lines = new List<string>();
        foreach (var entry in result.Events.Where(e => e.Kind == "rumble-boundary"))
        {
            var data = (JsonElement)entry.Data;
            string felt = data.GetProperty("LowestFelt").ToString();
            string missed = data.GetProperty("FirstNotFelt").ToString();
            lines.Add($"Motor {data.GetProperty("Motor").GetInt32() + 1} · {data.GetProperty("Phase").GetString()}: " +
                (felt.Length == 0 ? "nothing felt at the highest test level" : "lowest felt " + felt + " " + data.GetProperty("Units").GetString() + (missed.Length == 0 ? "; all test levels felt" : "; first not felt " + missed)));
        }
        return string.Join("\n", lines) + "\n\nThese are your observed boundaries, not assumed defaults.";
    }
    private void ControllerAnswer()
    {
        if (!_feedback || _pending is null)
        {
            return;
        }

        int connected = 0; ushort buttons = 0;
        for (uint i = 0; i < 4; i++)
        {
            if (InputCapture.XInputGetState(i, out var state) == 0) { connected++; buttons = state.Gamepad.Buttons; }
        }

        if (connected != 1) { _controllerReleased = false; return; }
        int ab = buttons & 0x3000;
        if (ab == 0) { _controllerReleased = true; return; }
        if (!_controllerReleased)
        {
            return;
        }

        _controllerReleased = false;
        if (ab == 0x1000)
        {
            Answer("felt");
        }
        else if (ab == 0x2000)
        {
            Answer("not-felt");
        }
    }
    private async Task Finish()
    {
        Chapter(7, "Save the report"); _progress.Value = 100;
        RestoreHidHide();
        string pending = File.Exists(_session.RecoveryPath) ? "\nRestoration still needs checking; the recovery record remains on this PC.\n" : "";
        try
        {
            string answer = await Ask(_stopping ? "Progress saved" : "Session finished", $"{_endReason}\n{_session.Results.Count} test/capture results saved. Skipped and incomplete steps remain marked as such.{pending}\nUse Capture files to review the JSON. The ZIP contains model/BIOS, raw ASUS reports, sensor values, control readbacks and your notes. Save it to send back?", ("export", "Save ZIP to Desktop"), ("close", "Close without exporting"));
            if (answer == "export")
            {
                string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AllyXLab-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ".zip");
                _session.Export(file);
                string next = await Ask("Send this ZIP back", file + pending, ("folder", "Open ZIP folder"), ("close", "Close"));
                if (next == "folder")
                {
                    OpenFolder(Path.GetDirectoryName(file)!);
                }
            }
            Close();
        }
        catch (Exception e)
        {
            await Ask("Report files are still saved", e.Message + "\n\nYour original JSON files are in:\n" + _session.DirectoryPath, ("close", "Close"));
            Close();
        }
    }
    private void ShowDetails() => ShowText("Current step details", _detail);
    private void ShowText(string title, string text)
    {
        using var dialog = new Form { Text = title, Size = new Size(850, 650), StartPosition = FormStartPosition.CenterParent };
        dialog.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Text = text, WordWrap = false });
        dialog.ShowDialog(this);
    }
    private static void OpenFolder(string path) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
}
