using System.Text.Json;

namespace WSGM.AllyXLab;

internal sealed class MainForm : Form
{
    private readonly Session _session = new();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TextBox _output = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8), Text = "Start with inventory. No hardware test runs automatically." };
    private readonly ComboBox _rumbleEndpoint = new() { Width = 510, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _rgbEndpoint = new() { Width = 510, DropDownStyle = ComboBoxStyle.DropDownList };
    private bool _busy;
    private bool _cancelRequested;
    private Request? _lastRumbleRequest;
    private readonly List<Button> _runButtons = [];
    private readonly ListBox _steps = new() { Width = 430, Height = 360 };
    private readonly Label _stepHelp = new() { Width = 520, Height = 105 };
    private readonly List<(string Name, string Instruction)> _captureSteps = [];
    private readonly ComboBox _phase = new() { Width = 380, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _motor = new() { Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _rumbleLevel = new() { Minimum = 1, Maximum = 50, Value = 30, Width = 90 };
    private readonly NumericUpDown _pulse = new() { Minimum = 5, Maximum = 2000, Value = 250, Width = 90 };
    private readonly Label _rumbleHint = new() { Width = 800, Height = 65 };
    private int _sweepIndex;
    private int _lastRumbleSequence;
    private bool _awaitingRumbleFeedback;
    private readonly int[] _strengths = [50, 44, 38, 32, 25, 22, 19, 16, 13, 11, 9, 8, 6, 5, 3, 1];
    private readonly int[] _durations = [200, 150, 120, 90, 70, 55, 45, 35, 28, 22, 17, 13, 10, 7, 5];

    private sealed record Choice(string Text, string Id) { public override string ToString() => Text; }
    internal MainForm()
    {
        Text = "ROG Ally X Lab • attended hardware mapping • 0.1.0";
        Size = new Size(1050, 820); MinimumSize = new Size(850, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(5) };
        var stop = new Button { Text = "STOP / restore", AutoSize = true, BackColor = Color.MistyRose };
        stop.Click += (_, _) => { _cancelRequested = true; _session.Cancel(); _status.Text = "Stopping. Wait for restoration; do not unplug the device or close the app."; };
        var export = new Button { Text = "Review / export ZIP", AutoSize = true };
        export.Click += (_, _) => Export();
        var folder = new Button { Text = "Open capture folder", AutoSize = true };
        folder.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_session.DirectoryPath) { UseShellExecute = true });
        var help = new Button { Text = "Instructions / licenses", AutoSize = true };
        help.Click += (_, _) =>
        {
            var assembly = typeof(MainForm).Assembly;
            var text = new System.Text.StringBuilder();
            foreach (string resource in assembly.GetManifestResourceNames().Order())
            {
                using var stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                text.AppendLine(resource).AppendLine(reader.ReadToEnd()).AppendLine();
            }
            _output.Text = text.ToString(); _tabs.SelectedIndex = _tabs.TabCount - 1;
        };
        top.Controls.AddRange([stop, export, folder, help]);
        Controls.Add(_tabs); Controls.Add(_status); Controls.Add(top);
        BuildOverview(); BuildCapture(); BuildPower(); BuildRumble(); BuildRgb();
        var reportTab = new TabPage("Results"); reportTab.Controls.Add(_output); _tabs.TabPages.Add(reportTab);
        FormClosing += (_, e) =>
        {
            if (_busy) { e.Cancel = true; _cancelRequested = true; _session.Cancel(); _status.Text = "Stopping and restoring. Close again after the result appears."; }
            else if (File.Exists(_session.RecoveryPath) && MessageBox.Show("Restoration still needs confirmation. The recovery file will remain. Close anyway?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                e.Cancel = true;
            }
        };
    }
    private FlowLayoutPanel Page(string title)
    {
        var page = new TabPage(title);
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16) };
        page.Controls.Add(panel); _tabs.TabPages.Add(page); return panel;
    }
    private static Label TextBlock(string text, int height = 75) => new() { Text = text, Width = 900, Height = height };
    private Button RunButton(FlowLayoutPanel panel, string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(160, 38) };
        button.Click += async (_, _) =>
        {
            if (_busy)
            {
                return;
            }

            try { await action(); }
            catch (Exception e) { MessageBox.Show(e.Message, "Step could not complete", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        _runButtons.Add(button); panel.Controls.Add(button); return button;
    }
    private void BuildOverview()
    {
        var panel = Page("1 • Start");
        panel.Controls.Add(TextBlock("This experimental tool maps the original ROG Ally X (RC72LA), not the Xbox Ally X. Run on the tester's device only. Nothing is installed and no controller remapping is performed.", 60));
        panel.Controls.Add(TextBlock("Before testing: disconnect other controllers; close games, WSGM, Handheld Companion, G-Helper and Armoury Crate. Keep AC/DC state unchanged during each action. Start above 30% battery. Put the device on a hard surface with clear vents. Do not run a stress test.", 85));
        panel.Controls.Add(TextBlock("Work through the tabs. Each capture/test is separate and repeatable. A missing getter or unexpected identity is useful evidence: export the report instead of trying to bypass the refusal. Hardware controls are derived from HHD and cross-checked against HC, but have not yet been validated on an Ally X with this tool.", 90));
        RunButton(panel, "Collect identity and interfaces", async () =>
        {
            var result = await Run(new(ActionKind.Inventory, "Initial inventory"));
            if (result is not null)
            {
                UpdateEndpoints(result);
            }
        });
        RunButton(panel, "Read power / fan state", async () => { await Run(new(ActionKind.ReadPower, "Initial read-only power and fans")); });
        panel.Controls.Add(TextBlock("Recovery: every mutation saves the original state before writing. If a worker hangs, the app marks cleanup UNKNOWN and blocks further writes. Read recovery-required.json, restore the recorded profile/limits/curves with the OEM controls, and restart if needed. RGB/motor zero output also needs your visual/touch confirmation.", 90));
        RunButton(panel, "Review unresolved recovery", () =>
        {
            if (!File.Exists(_session.RecoveryPath)) { MessageBox.Show("No pending recovery record."); return Task.CompletedTask; }
            string text = File.ReadAllText(_session.RecoveryPath);
            _output.Text = text; _tabs.SelectedIndex = _tabs.TabCount - 1;
            if (MessageBox.Show("The recovery record is shown in Results. Have you restored the recorded original settings using OEM controls and confirmed motors are silent / lights match the original state?\n\nOnly answer Yes after checking.", "Manual recovery confirmation", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                _session.ConfirmRecovery("Tester confirms manual OEM restoration after reviewing recovery record.");
            }

            return Task.CompletedTask;
        });
        panel.Controls.Add(TextBlock("The exported ZIP includes identity (model, board, SKU, BIOS), interface metadata with hashed paths, raw ASUS button reports, sensor fields, control readbacks and your observations. Review the files before sharing. No automatic upload occurs.", 70));
    }
    private void BuildCapture()
    {
        var panel = Page("2 • Buttons / motion");
        _captureSteps.Add(("Neutral", "Leave every button released and both sticks centered. Keep the device still for the whole capture."));
        foreach (string name in new[] { "A", "B", "X", "Y", "D-pad up", "D-pad down", "D-pad left", "D-pad right", "Left bumper", "Right bumper", "View", "Menu", "Left stick click", "Right stick click", "Command Center", "Armoury Crate", "Rear M1", "Rear M2", "Volume up", "Volume down" })
        {
            _captureSteps.Add((name, $"Press {name} three times separately, then hold it for two seconds and release. Leave a second between presses. Do not touch other controls."));
        }

        foreach (string name in new[] { "Left stick", "Right stick", "Left trigger", "Right trigger" })
        {
            _captureSteps.Add((name + " travel", $"Move {name} slowly through full travel and back to neutral. For a stick include all four edges and a full circle. For a trigger include half travel."));
        }

        _captureSteps.Add(("D-pad diagonals", "Press each diagonal separately, releasing between directions."));
        _captureSteps.Add(("Rear M1 + A", "Hold M1, press/release A three times, then release M1. Repeat once in reverse press order."));
        _captureSteps.Add(("Rear M2 + A", "Hold M2, press/release A three times, then release M2. Repeat once in reverse press order."));
        _captureSteps.Add(("Rollover A + B + LB + RB", "Hold A, add B, LB and RB one at a time, then release in reverse order. Repeat."));
        _captureSteps.Add(("Power button / resume (optional)", "Press power once to sleep, then wake the device promptly. A timeout is a valid incomplete result. This observes Windows power events, not raw physical attribution. Do not hold the power button or force shutdown."));
        _captureSteps.Add(("Gyro stationary bias", "Put the device flat on a stable surface. Do not touch it. Capture lasts 15 seconds. Repeat after the device warms up; means/stddev are candidates, not firmware calibration."));
        foreach (string pose in new[] { "Screen up", "Screen down", "Left edge down", "Right edge down", "Top edge down", "Bottom edge down" })
        {
            _captureSteps.Add(("Gravity: " + pose, $"Hold the device still with {pose.ToLowerInvariant()}. Support it safely without blocking vents. This captures accelerometer offset, scale and axis evidence."));
        }

        foreach (string rotation in new[] { "Yaw: screen up, rotate clockwise on the table", "Pitch: tilt top edge away from you", "Roll: lower right edge" })
        {
            _captureSteps.Add((rotation, "Start still. Rotate approximately 90 degrees in the stated direction over two seconds, hold, then return slowly. This establishes gyro sign and axis; do not shake."));
        }

        _steps.Items.AddRange(_captureSteps.Select(s => s.Name).ToArray()); _steps.SelectedIndex = 0;
        _steps.SelectedIndexChanged += (_, _) => _stepHelp.Text = _captureSteps[Math.Max(0, _steps.SelectedIndex)].Instruction;
        _stepHelp.Text = _captureSteps[0].Instruction;
        var row = new FlowLayoutPanel { Width = 970, Height = 370, WrapContents = false }; row.Controls.Add(_steps); row.Controls.Add(_stepHelp); panel.Controls.Add(row);
        panel.Controls.Add(TextBlock("The countdown gives you time to position the device. Input is recorded only during the selected step. OEM buttons may launch their normal Windows action. Rear-button remapping is intentionally not written without a verified way to restore the original mapping; native M1/M2 reports and chords are captured first.", 80));
        RunButton(panel, "Start selected capture", async () =>
        {
            var step = _captureSteps[_steps.SelectedIndex];
            await Run(new(ActionKind.Capture, step.Name, Seconds: step.Name.Contains("Gyro") ? 15 : 10), countdown: true);
        });
        RunButton(panel, "Next step", () => { if (_steps.SelectedIndex < _steps.Items.Count - 1) { _steps.SelectedIndex++; } return Task.CompletedTask; });
        RunButton(panel, "Add observation / unavailable step", () => { Note(_captureSteps[_steps.SelectedIndex].Name); return Task.CompletedTask; });
    }
    private void BuildPower()
    {
        var panel = Page("3 • Power / fans");
        panel.Controls.Add(TextBlock("Every write requires stable original profile, SPL/SPPT/FPPT and both fan-curve readbacks. Results include four readback samples, raw ASUS responses, and restoration readback. An accepted driver call is not counted as a successful setting. Fan readings retain driver units; they are not labeled RPM without validation.", 90));
        RunButton(panel, "Read current profiles, limits and curves", async () => { await Run(new(ActionKind.ReadPower, "Read-only controls")); });
        var watts = new NumericUpDown { Minimum = 5, Maximum = 25, Value = 13, Width = 100 };
        panel.Controls.Add(TextBlock("TDP test: set SPL, SPPT and FPPT to the same value (5–25 W), observe for four seconds, then restore. Start at 13 W, then 17 W; 25 W is optional. Repeat the read/profile tests on battery in a separate step after changing AC/DC state.", 70));
        panel.Controls.Add(watts);
        RunButton(panel, "Set TDP envelope → read → restore", async () => { await Run(new(ActionKind.Tdp, $"TDP envelope {watts.Value} W", (int)watts.Value)); });
        var profiles = new ComboBox { Width = 350, DropDownStyle = ComboBoxStyle.DropDownList };
        profiles.Items.AddRange(["Performance (firmware 0)", "Turbo (firmware 1)", "Silent (firmware 2)"]); profiles.SelectedIndex = 2;
        panel.Controls.Add(TextBlock("Firmware profiles: select one profile; record what all three limits and both curves actually become. The tool does not replace those values with HHD/HC preset numbers. Then restore the previous profile and custom state.", 65)); panel.Controls.Add(profiles);
        RunButton(panel, "Set firmware profile → read → restore", async () => { await Run(new(ActionKind.Profile, profiles.Text, profiles.SelectedIndex)); });
        var fan = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList }; fan.Items.AddRange(["CPU fan", "GPU fan"]); fan.SelectedIndex = 0; panel.Controls.Add(fan);
        panel.Controls.Add(TextBlock("Fan test raises the selected curve's duty points by 15 percentage points (capped at 99), leaving its temperatures unchanged. It never lowers cooling. Readback proves the curve setting; use the observation field to report which fan audibly changed. The captured original curve is restored.", 80));
        RunButton(panel, "Raise fan curve → read → restore", async () => { await Run(new(ActionKind.Fan, fan.Text, 15, fan.SelectedIndex)); Note(fan.Text + " audible result"); });
    }
    private void BuildRumble()
    {
        var panel = Page("4 • Rumble calibration");
        panel.Controls.Add(TextBlock("Based on the Claw's three phases: sustained strength, 30 ms tick strength, and pulse duration. Here each motor is tested separately and each sustained step lasts 1.5 seconds. Levels are capped at 50% for this first Ally X bring-up. A measured floor is subjective; the hardware has no perceptual readback.", 85));
        panel.Controls.Add(_rumbleEndpoint);
        _motor.Items.AddRange(["Weak channel", "Strong channel"]); _motor.SelectedIndex = 0; panel.Controls.Add(_motor);
        _phase.Items.AddRange(["1 • Sustained strength (1.5 seconds)", "2 • Tick strength (30 ms)", "3 • Pulse duration (50% drive)", "Manual pulse"]); _phase.SelectedIndex = 0;
        _phase.SelectedIndexChanged += (_, _) => { _sweepIndex = 0; _awaitingRumbleFeedback = false; UpdateSweep(); };
        _motor.SelectedIndexChanged += (_, _) => { _sweepIndex = 0; _awaitingRumbleFeedback = false; UpdateSweep(); };
        panel.Controls.Add(_phase); panel.Controls.Add(_rumbleHint);
        panel.Controls.Add(TextBlock("Manual: intensity % and pulse length in ms. For the guided phases these values are filled automatically. Keep the device in your hands and report both the felt location and strength.", 60));
        var row = new FlowLayoutPanel { Width = 500, Height = 40 }; row.Controls.Add(_rumbleLevel); row.Controls.Add(_pulse); panel.Controls.Add(row);
        RunButton(panel, "Fire selected step", FireRumble);
        RunButton(panel, "Felt it → record / step weaker", () => { RumbleFeedback(true); return Task.CompletedTask; });
        RunButton(panel, "Did not feel it → record boundary", () => { RumbleFeedback(false); return Task.CompletedTask; });
        RunButton(panel, "Add motor location / comfort note", () => { Note("Rumble motor location/comfort"); return Task.CompletedTask; });
        UpdateSweep();
    }
    private void UpdateSweep()
    {
        if (_phase.SelectedIndex < 0)
        {
            return;
        }

        int count = _phase.SelectedIndex == 2 ? _durations.Length : _strengths.Length;
        if (_sweepIndex >= count) { _rumbleHint.Text = "Phase finished. If every step was felt, the true boundary is below the last tested value. Select the next phase/motor."; return; }
        if (_phase.SelectedIndex < 3)
        {
            _rumbleLevel.Value = _phase.SelectedIndex == 2 ? 50 : _strengths[_sweepIndex];
            _pulse.Value = _phase.SelectedIndex switch { 0 => 1500, 1 => 30, _ => _durations[_sweepIndex] };
        }
        _rumbleHint.Text = $"Step {_sweepIndex + 1}: {_rumbleLevel.Value}% for {_pulse.Value} ms. Fire once, then record Felt / Not felt. No step fires automatically.";
    }
    private async Task FireRumble()
    {
        if (_awaitingRumbleFeedback)
        {
            throw new InvalidOperationException("Record Felt / Not felt for the previous pulse first.");
        }

        if (_sweepIndex >= (_phase.SelectedIndex == 2 ? _durations.Length : _strengths.Length) && _phase.SelectedIndex != 3)
        {
            return;
        }

        string endpoint = (_rumbleEndpoint.SelectedItem as Choice)?.Id ?? "";
        var result = await Run(new(ActionKind.Rumble, _phase.Text + " / " + _motor.Text, (int)_rumbleLevel.Value, _motor.SelectedIndex, endpoint, PulseMilliseconds: (int)_pulse.Value));
        if (result?.Error is null && result?.Outcome == "write-returned-awaiting-operator-observation")
        { _awaitingRumbleFeedback = true; _lastRumbleSequence = _session.Results.Count; _lastRumbleRequest = result.Request; }
    }
    private void RumbleFeedback(bool felt)
    {
        if (!_awaitingRumbleFeedback)
        {
            return;
        }

        _session.Observation("rumble-calibration", new { ResultNumber = _lastRumbleSequence, PhaseAndMotor = _lastRumbleRequest!.Label, MotorChannel = _lastRumbleRequest.Channel, IntensityPercent = _lastRumbleRequest.Value, DurationMs = _lastRumbleRequest.PulseMilliseconds, Felt = felt, Interpretation = felt ? "This step was felt; boundary remains open" : "First not-felt candidate; use previous confirmed step, repeat to establish boundary" });
        _awaitingRumbleFeedback = false;
        _sweepIndex = felt ? _sweepIndex + 1 : (_phase.SelectedIndex == 2 ? _durations.Length : _strengths.Length);
        UpdateSweep();
    }
    private void BuildRgb()
    {
        var panel = Page("5 • RGB");
        panel.Controls.Add(TextBlock("First use the OEM controls to set lighting OFF, then close the other manager. The test uses HHD's 0x5A output-report path only. It does not probe alternate feature reports. It flashes a low-brightness primary color for two seconds and sends brightness OFF in cleanup.", 90));
        panel.Controls.Add(TextBlock("There is no established current-color readback. OFF is your confirmed baseline, not an automatically captured original color/mode. The test may change the remembered lighting mode/color; use OEM controls to restore those afterward. Results require visual confirmation and are never labeled hardware-verified RGB restoration.", 90));
        panel.Controls.Add(_rgbEndpoint);
        var zone = new ComboBox { Width = 360, DropDownStyle = ComboBoxStyle.DropDownList };
        zone.Items.AddRange(["All zones", "Left ring outer half", "Left ring inner half", "Right ring inner half", "Right ring outer half"]); zone.SelectedIndex = 0; panel.Controls.Add(zone);
        var color = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList }; color.Items.AddRange(["Red", "Green", "Blue"]); color.SelectedIndex = 0; panel.Controls.Add(color);
        RunButton(panel, "Flash color → OFF → confirm", async () =>
        {
            await Run(new(ActionKind.Rgb, $"{zone.Text} / {color.Text}", color.SelectedIndex, zone.SelectedIndex, (_rgbEndpoint.SelectedItem as Choice)?.Id ?? ""));
            Note("RGB actual zone/color and any flicker");
        });
    }
    private void UpdateEndpoints(Result result)
    {
        _rumbleEndpoint.Items.Clear(); _rgbEndpoint.Items.Clear();
        foreach (LabEvent item in result.Events.Where(e => e.Kind == "endpoints"))
        {
            JsonElement data = (JsonElement)item.Data;
            foreach (var e in data.EnumerateArray())
            {
                if (e.GetProperty("Vid").GetInt32() != 0x0B05 || e.GetProperty("Pid").GetInt32() != 0x1B4C)
                {
                    continue;
                }

                int page = e.GetProperty("Page").GetInt32(), usage = e.GetProperty("Usage").GetInt32(), bytes = e.GetProperty("OutputBytes").GetInt32();
                string id = e.GetProperty("Id").GetString()!;
                var choice = new Choice($"{id} • usage {page:X4}:{usage:X4} • output {bytes} bytes", id);
                if (page == 0xFF31 && usage == 0x80)
                {
                    _rgbEndpoint.Items.Add(choice);
                }

                if ((page == 1 && usage == 5) || (page == 15 && usage is 2 or 0x21))
                {
                    _rumbleEndpoint.Items.Add(choice);
                }
            }
        }
        if (_rgbEndpoint.Items.Count == 1)
        {
            _rgbEndpoint.SelectedIndex = 0;
        }

        if (_rumbleEndpoint.Items.Count == 1)
        {
            _rumbleEndpoint.SelectedIndex = 0;
        }
    }
    private async Task<Result?> Run(Request request, bool countdown = false)
    {
        string effect = request.Action switch
        {
            ActionKind.Rgb => "Confirm the lights are OFF and you accept changing the remembered RGB color/mode. Flash the selected zone, then turn brightness OFF. Restore your previous lighting settings with OEM controls afterward.",
            ActionKind.Rumble => "Confirm the motors are silent. Pulse the selected motor, then send zero output. STOP cancels the pulse.",
            ActionKind.Tdp or ActionKind.Profile or ActionKind.Fan => "Read/save original state, perform the selected hardware write, read back, then restore. Keep power connected/disconnected exactly as it is now. STOP requests restoration.",
            _ => "Observe identity and selected device signals. No hardware settings will be changed.",
        };
        if (MessageBox.Show(request.Label + "\n\n" + effect, "Run this step?", MessageBoxButtons.OKCancel, Limits.Mutates(request.Action) ? MessageBoxIcon.Warning : MessageBoxIcon.Information) != DialogResult.OK)
        {
            return null;
        }

        _cancelRequested = false;
        _busy = true; foreach (var button in _runButtons)
        {
            button.Enabled = false;
        }

        try
        {
            if (countdown)
            {
                for (int n = 3; n > 0; n--) { _status.Text = $"Get ready: {n}…"; await Task.Delay(1000); if (_cancelRequested) { _status.Text = "Capture cancelled before acquisition."; return null; } }
            }

            if (_cancelRequested)
            {
                return null;
            }

            _status.Text = $"Running: {request.Label}. Capture window: {request.Seconds} seconds; controls also include preflight/readback/restoration.";
            Result result = await _session.RunAsync(request, text => _status.Text = text);
            string summary = $"{request.Label}\r\nOutcome: {result.Outcome}\r\nCleanup: {result.Cleanup}\r\n{result.Error}\r\nEvents: {result.Events.Count}\r\n";
            _output.Text = summary + JsonSerializer.Serialize(result, SessionLog.Json);
            _status.Text = summary.Replace("\r\n", " • ");
            if (result.Cleanup.StartsWith("zero-output-sent", StringComparison.Ordinal))
            {
                string question = request.Action == ActionKind.Rgb ? "Are the lights OFF again? This confirms visible OFF only, not restoration of the remembered mode/color." : "Are both motors completely silent now?";
                bool confirmed = MessageBox.Show(question, "Confirm zero output", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                _session.Observation(request.Label + " cleanup", new { OperatorConfirmed = confirmed, question });
                if (confirmed)
                {
                    _session.ConfirmRecovery("Tester confirmed zero output after " + request.Label);
                }
            }
            if (result.Error is not null || result.Cleanup.Contains("FAILED", StringComparison.Ordinal))
            {
                MessageBox.Show(summary + "\nExport this result. Do not retry an uncertain write. Review any recovery record before continuing.", "Step result", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return result;
        }
        finally
        {
            _busy = false; foreach (var button in _runButtons)
            {
                button.Enabled = true;
            }
        }
    }
    private void Note(string label)
    {
        using var dialog = new Form { Text = "Tester observation • " + label, Size = new Size(660, 340), StartPosition = FormStartPosition.CenterParent };
        var input = new TextBox { Multiline = true, Dock = DockStyle.Fill, MaxLength = 2000 };
        var save = new Button { Text = "Save observation", Dock = DockStyle.Bottom, Height = 45, DialogResult = DialogResult.OK };
        dialog.Controls.Add(input); dialog.Controls.Add(save);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _session.Observation(label, input.Text);
        }
    }
    private void Export()
    {
        if (_busy) { MessageBox.Show("Wait for the current step to finish restoring."); return; }
        if (MessageBox.Show("Review the JSON files in the capture folder before sharing. They contain model/BIOS/SKU, raw ASUS input reports, sensor values, hardware readbacks and your notes. Export this session?", "Review export", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
        {
            return;
        }

        using var save = new SaveFileDialog { Filter = "ZIP capture|*.zip", FileName = "AllyXLab-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip", OverwritePrompt = true };
        if (save.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try { _session.Export(save.FileName); MessageBox.Show("Send this ZIP back:\n" + save.FileName); }
        catch (Exception e) { MessageBox.Show(e.Message); }
    }
}
