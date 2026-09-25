using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Transports;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Gui;

// The power stage: read-only telemetry for every device (battery, charger, temperatures, CPU clock,
// power plan and fan speed, before and after a 20 second load burst), then the device's own power,
// performance mode, fan and charge-limit tests when the confirmed record is curated and the transport
// opens, then lighting (Windows Dynamic Lighting for any device, plus the curated Aura endpoint). Every
// hardware call runs in the elevated worker. Before a transport's first write the worker captures the
// original itself (the checkpoint), the stage records it in LabMachineState, and only then does the
// worker accept writes; each write is read back and put back in a safe order, and the checkpoint is
// released after a verified restore. An uncertain write is never retried, and an unverified restore is
// shown in orange with a "Try restoring again" button. Everything the stage changed is restored when it
// ends, however it ends.
internal sealed partial class WizardWindow
{
    private static readonly TimeSpan LoadBurst = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LoadTest = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    private static readonly string[] SeenColours = ["Red", "Green", "Blue", "Off", "Nothing changed"];
    private static readonly string[] LouderAnswers = ["Yes", "No", "Not sure"];

    // The charger state a write is pinned to, set by the pre-write gate. A write is abandoned if the
    // charger state changes, because a limit captured on one power source must not be replayed on the
    // other.
    private int _pinnedAcLine = -1;

    // The curated record the stage's device writes belong to; its identity is rechecked before each write.
    private DeviceKnowledgeRecord? _powerRecord;

    // The power stage's LibreHardwareMonitor session; open only while the stage runs.
    private LabLhmSensors? _powerSensors;

    /// <summary>Whether a power, fan or charge change is recorded that has not been confirmed as put back.</summary>
    /// <remarks>
    ///     Call this from the window's <c>Closing</c> handler; when it is true, ask the tester
    ///     "Close without confirming restoration?" before closing.
    /// </remarks>
    public bool PowerRestorationPending()
    {
        return LabPowerChanges.PowerPending(_machine.Read().Power);
    }

    private async Task RunPowerAsync(LabProject project, StackPanel page)
    {
        page.Children.Add(Status("Reading the power, temperature and fan state. Nothing is changed yet."));
        var attempt = await Task.Run(() => project.BeginAttempt(LabStages.Power, DateTimeOffset.UtcNow));
        var record = ConfirmedRecord(project);
        var plan = LabPowerPlan.For(record);
        LabPowerLog log = new();
        List<LabPowerSample> telemetry = [];
        List<LabPowerTestResult> tests = [];
        List<(string Shown, string Seen)> lighting = [];
        var lightingFound = false;
        _powerRecord = plan.Curated ? record : null;
        if (_options.Elevated)
        {
            // Every hardware read and write of this stage runs in the worker, including the fan readers.
            await WorkerAsync();
        }

        if (_powerRecord is { } curated)
        {
            // The BIOS, EC and vendor endpoint facts every later write is checked against.
            var fingerprint = await Task.Run(() => LabPowerIdentity.Begin(curated));
            await Task.Run(() => WriteEvidenceOnce(project, attempt, "identity-baseline", fingerprint));
        }

        try
        {
            // Every device: LibreHardwareMonitor's fan RPM and temperatures, opened once for the stage
            // and read with every sample next to the vendor readings. It only reads; a session that did
            // not open reports why in each sample's unavailable list.
            _powerSensors = await LabLhmSensors.OpenAsync(Lifetime);

            // Every device: read-only telemetry, before and during a load burst.
            telemetry.AddRange(await RunTelemetryAsync(page, plan, "before-load"));
            telemetry.AddRange(await RunLoadBurstAsync(page, plan));

            // Device write tests: only from a curated record whose transport opens (and, for the
            // power, fan and charge tests, only when elevated).
            if (plan.EmbeddedController.Count > 0 && !plan.HasDeviceTests)
            {
                ShowEmbeddedControllerNote(page, plan);
            }

            if (plan.HasDeviceTests)
            {
                await RunDeviceTestsAsync(project, attempt, page, plan, log, tests, telemetry, "ac-or-battery");
                await RunPowerSourceRepeatAsync(project, attempt, page, plan, log, tests, telemetry);
            }
            else if (_options.Elevated)
            {
                // No curated interface: the processor's own power limit, through the SMU or KX.
                await RunDeviceTestsAsync(project, attempt, page, plan, log, tests, telemetry, "ac-or-battery");
                await RunPowerSourceRepeatAsync(project, attempt, page, plan, log, tests, telemetry);
            }
            else if (record is null)
            {
                page.Children.Add(Muted(
                    "This device is not in the list, so only the readings above and the lights below are tested."));
            }
            else if (!plan.Curated)
            {
                page.Children.Add(Muted(
                    "This device is known but not fully confirmed, so only the readings above and the lights below are tested."));
            }

            if (plan.Untested.Count > 0)
            {
                page.Children.Add(Muted(
                    "No test for this build covers: "
                    + string.Join(", ",
                        plan.Untested.Select(mechanism => $"{mechanism.Feature} ({mechanism.Transport})"))
                    + ". They are recorded for the maintainer."));
            }

            // Lighting: generic Dynamic Lighting for any device, plus the curated Aura endpoint.
            lightingFound = await RunLightingAsync(page, plan, log, lighting);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            page.Children.Add(Warning($"Something went wrong: {ex.Message}"));
            log.Add("stage-error", ex.Message);
        }
        finally
        {
            // On success, failure and window close alike: put back everything still recorded.
            await RestorePendingAsync(page, tests);
            var sensors = _powerSensors;
            _powerSensors = null;
            if (sensors is not null)
            {
                await Task.Run(sensors.Dispose);
            }

            await Task.Run(() =>
            {
                project.WriteEvidence(attempt, "power-telemetry", new { Samples = telemetry, Events = log.Events() });
                project.WriteEvidence(attempt, "power-tests", new { Tests = tests });
                project.WriteEvidence(attempt, "lighting",
                    new { Found = lightingFound, Shown = lighting.Select(item => new { item.Shown, item.Seen }) });
            });
        }

        var summary = $"{LabPowerSummary.Power(tests, "power, temperatures and fans measured")}. "
                      + $"{LabPowerSummary.Lighting(lighting, lightingFound)}.";
        await Task.Run(() =>
            project.Finish(LabStages.Power, LabSegmentStatus.Completed, summary, DateTimeOffset.UtcNow));

        var pending = LabPowerChanges.PowerPending(_machine.Read().Power);
        page.Children.Add(Buttons(
            Action("Run again", () => StartStage(LabStages.Power)),
            Action(pending ? "Continue anyway" : "Continue", () => Next(LabStages.Power))));
    }

    private async Task<IReadOnlyList<LabPowerSample>> RunTelemetryAsync(StackPanel page, LabPowerPlan plan,
        string label)
    {
        var sample = await Task.Run(() => ReadTelemetry(label, plan));
        page.Children.Add(Status($"Now: {LabPowerTelemetry.Describe(sample)}."));
        foreach (var missing in sample.Unavailable)
        {
            page.Children.Add(Muted($"Could not read {missing}."));
        }

        return [sample];
    }

    private async Task<IReadOnlyList<LabPowerSample>> RunLoadBurstAsync(StackPanel page, LabPowerPlan plan)
    {
        var line = Status("The fan may get louder for 20 seconds while the processor is kept busy.");
        page.Children.Add(line);
        var samples = await LabPowerLoad.RunAsync(LoadBurst, SampleInterval,
            label => ReadTelemetry(label, plan),
            left => OnUi(() => line.Text = $"Keeping the processor busy... {left} seconds left."),
            Lifetime);
        var after = await Task.Run(() => ReadTelemetry("after-load", plan));
        line.Text = $"After the load: {LabPowerTelemetry.Describe(after)}.";
        return [.. samples, after];
    }

    private LabPowerSample ReadTelemetry(string label, LabPowerPlan plan)
    {
        return LabPowerTelemetry.Read(label, FanReader(plan), _powerSensors);
    }

    private Func<IReadOnlyList<LabFanReading>>? FanReader(LabPowerPlan plan)
    {
        // A fan RPM source the record names and the transport can read, without touching any write path.
        // It opens a worker session per reading and never takes a checkpoint, so it cannot write.
        if (_worker is not { } worker || !_options.Elevated)
        {
            return null;
        }

        if (plan is { Curated: true, Msi.FanGetter: not null })
        {
            return () =>
            {
                try
                {
                    using var wmi = worker.Open<ILabMsiWmi>(LabMsiWmi.Service.Name, null, plan.Msi!);
                    return wmi.FanSpeeds();
                }
                catch (Exception ex) when (LabMsiWmi.IsTransportFailure(ex))
                {
                    return [];
                }
            };
        }

        if (plan is { Curated: true, Asus: not null }
            && (plan.Asus.CpuSpeed is not null || plan.Asus.GpuSpeed is not null))
        {
            return () =>
            {
                try
                {
                    using var acpi = worker.Open<ILabAtkAcpi>(LabAtkAcpi.Service.Name, null, plan.Asus!);
                    return acpi.FanSpeeds();
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
                {
                    return [];
                }
            };
        }

        return null;
    }

    private void ShowEmbeddedControllerNote(StackPanel page, LabPowerPlan plan)
    {
        page.Children.Add(Heading("Fans and power"));
        page.Children.Add(Status(
            "Fan and power control on this device go through the embedded controller; this build only records telemetry for it."));
        var registers = plan.EmbeddedController
            .SelectMany(mechanism =>
                mechanism.Parameters.Select(pair => $"{mechanism.Feature}: {pair.Key} = {pair.Value}"))
            .ToArray();
        if (registers.Length > 0)
        {
            page.Children.Add(Muted("For the maintainer: " + string.Join("; ", registers)));
        }
    }

    private async Task RunDeviceTestsAsync(LabProject project, string attempt, StackPanel page, LabPowerPlan plan,
        LabPowerLog log, List<LabPowerTestResult> tests, List<LabPowerSample> telemetry, string passLabel)
    {
        page.Children.Add(Heading($"Power and fan tests ({SourceName(passLabel)})"));
        if (!_options.Elevated)
        {
            page.Children.Add(Status("Skipped: changing power, fans or the charge limit needs administrator rights."));
            return;
        }

        if (!plan.HasDeviceTests)
        {
            await RunProcessorPowerAsync(project, attempt, page, plan, tests, telemetry, passLabel);
            return;
        }

        // The recovery gate: a change an earlier test could not put back blocks every later write on this
        // device until the tester confirms they restored it.
        if (LabPowerChanges.PowerPending(_machine.Read().Power))
        {
            page.Children.Add(Warning(
                "An earlier test could not confirm it put the settings back, so no further power or fan test runs until they are restored."));
            await RestorePendingAsync(page, tests);
            if (LabPowerChanges.PowerPending(_machine.Read().Power))
            {
                return;
            }
        }

        // The pre-write checks: live identity, no competing manager, charger known, battery at least 30 %.
        var gate = await Task.Run(() => LabPowerGate.Check(plan.Record!));
        await Task.Run(() => WriteContext(project, attempt, $"context-{passLabel}", gate.Context));
        _pinnedAcLine = gate.AcLine;
        if (!gate.Ok)
        {
            page.Children.Add(Warning(gate.Problem ?? "The machine is not ready for a power test."));
            return;
        }

        if (plan.Asus is not null)
        {
            await RunAsusTestsAsync(project, attempt, page, plan, log, tests, telemetry, passLabel);
        }

        if (plan.Msi is not null)
        {
            await RunMsiTestsAsync(project, attempt, page, plan, log, tests, telemetry, passLabel);
        }
    }

    // Asks the tester to switch the charger and repeats the telemetry and TDP test on the other power
    // source, so the report shows how the device behaves plugged in and on battery.
    private async Task RunPowerSourceRepeatAsync(LabProject project, string attempt, StackPanel page,
        LabPowerPlan plan, LabPowerLog log, List<LabPowerTestResult> tests, List<LabPowerSample> telemetry)
    {
        if (!_options.Elevated || LabPowerChanges.PowerPending(_machine.Read().Power))
        {
            return;
        }

        var wasAc = LabPowerTelemetry.AcLine();
        if (wasAc is not (0 or 1))
        {
            return;
        }

        var wantPlugged = wasAc == 0;
        page.Children.Add(Heading("Now the other power source"));
        page.Children.Add(Status(wantPlugged
            ? "Plug the charger in now, then press Done to repeat the checks on AC power."
            : "Unplug the charger now, then press Done to repeat the checks on battery."));
        var choice = await AskAsync(page, "Done", "Skip this");
        if (choice == 1)
        {
            page.Children.Add(Muted("Skipped the second power source."));
            return;
        }

        if (LabPowerTelemetry.AcLine() == wasAc)
        {
            page.Children.Add(Warning("The charger state did not change, so the second power source was skipped."));
            return;
        }

        var label = wantPlugged ? "ac" : "battery";
        telemetry.AddRange(await RunTelemetryAsync(page, plan, $"before-load-{label}"));
        await RunDeviceTestsAsync(project, attempt, page, plan, log, tests, telemetry, label);
    }

    private async Task RunAsusTestsAsync(LabProject project, string attempt, StackPanel page, LabPowerPlan plan,
        LabPowerLog log, List<LabPowerTestResult> tests, List<LabPowerSample> telemetry, string passLabel)
    {
        var layout = plan.Asus!;
        var worker = await WorkerAsync();
        ILabAtkAcpi acpi;
        try
        {
            acpi = await Task.Run(() => worker.Open<ILabAtkAcpi>(LabAtkAcpi.Service.Name, log, layout));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            page.Children.Add(
                Status($"The ASUS power interface did not open, so its tests were skipped: {ex.Message}"));
            return;
        }

        using (acpi)
        {
            // Read-only first: every getter on its own, and both fan curves for all three modes.
            var getters = await Task.Run(acpi.ReadAllGetters);
            await Task.Run(() => WriteEvidenceOnce(project, attempt, $"asus-getters-{passLabel}", getters));
            if (!layout.CanSnapshot && layout.Charge is null)
            {
                return;
            }

            // The checkpoint: the worker captures the whole state itself and accepts no write until it is
            // recorded here, both in LabMachineState (for crash recovery) and in the project evidence.
            LabAsusOriginal original;
            string token;
            try
            {
                (original, token) = await Task.Run(() => worker.Checkpoint<LabAsusOriginal>(acpi, captured =>
                {
                    LabPowerRecovery.Record(_machine, plan.Record!.Id, changes => changes with
                    {
                        AcLine = _pinnedAcLine,
                        AsusPower = captured.Power ?? changes.AsusPower,
                        AsusChargeLimit = captured.ChargeLimit ?? changes.AsusChargeLimit
                    });
                    WriteEvidenceOnce(project, attempt, $"original-asus-{passLabel}",
                        new { _pinnedAcLine, State = captured });
                }));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                page.Children.Add(
                    Warning($"The current settings could not be recorded, so nothing was changed: {ex.Message}"));
                return;
            }

            var restored = true;
            if (layout.CanSnapshot)
            {
                if (original.Power is { } power)
                {
                    restored &= await RunAsusPowerFamilyAsync(page, plan, acpi, power, tests, telemetry, passLabel);
                }
                else
                {
                    page.Children.Add(Status(
                        $"The current power settings could not be read, so the tests were skipped: {original.PowerUnavailable}"));
                }
            }

            if (layout.Charge is not null)
            {
                restored &= await RunAsusChargeAsync(page, plan, acpi, original.ChargeLimit, tests);
            }

            if (restored)
            {
                await ReleaseQuietlyAsync(worker, acpi, token);
            }
        }
    }

    // Runs the TDP, performance mode and fan tests, then puts the whole family back once, together, and
    // clears the record when it reads back. Returns whether it was put back.
    private async Task<bool> RunAsusPowerFamilyAsync(StackPanel page, LabPowerPlan plan, ILabAtkAcpi acpi,
        LabAsusState original, List<LabPowerTestResult> tests, List<LabPowerSample> telemetry, string passLabel)
    {
        await RunAsusTdpAsync(page, plan, acpi, original, tests, telemetry, passLabel);
        await RunAsusProfileAsync(page, plan, acpi, original, tests);
        await RunAsusFanAsync(page, plan, acpi, original, tests);
        return await RestoreAsusPowerAsync(page, acpi, original, tests);
    }

    private async Task RunAsusTdpAsync(StackPanel page, LabPowerPlan plan, ILabAtkAcpi acpi, LabAsusState original,
        List<LabPowerTestResult> tests, List<LabPowerSample> telemetry, string passLabel)
    {
        foreach (var watts in LabPowerPlan.TestWattsList(plan.Asus!.MinimumWatts, plan.Asus.MaximumWatts))
        {
            if (!await PowerUnchangedAsync(page, tests, "tdp", "atkacpi"))
            {
                return;
            }

            page.Children.Add(
                Status($"Setting the power limit to {watts} watts for ten seconds, then putting it back..."));
            try
            {
                await Task.Run(() => acpi.SetLimits(original, watts));
                var layout = plan.Asus;
                var (matched, readbacks) = await ReadbackSamplesAsync(() =>
                {
                    int spl = acpi.Get(layout.Spl!.Value),
                        sppt = acpi.Get(layout.Sppt!.Value),
                        fppt = acpi.Get(layout.Fppt!.Value);
                    return (new { Spl = spl, Sppt = sppt, Fppt = fppt, Fans = acpi.FanSpeeds() },
                        spl == watts && sppt == watts && fppt == watts);
                });
                var loaded = await LabPowerLoad.RunAsync(LoadTest, SampleInterval,
                    label => ReadTelemetry($"tdp-{watts}w-{passLabel}-{label}", plan), null, Lifetime);
                telemetry.AddRange(loaded);
                tests.Add(new LabPowerTestResult
                {
                    Feature = "tdp",
                    Transport = "atkacpi",
                    Outcome = matched ? "applied-readback-matched" : "readback-mismatch",
                    Detail = $"{SourceName(passLabel)}: set {watts} W; four readbacks 1 s apart "
                             + (matched ? "all matched." : "did not all match."),
                    Original = new { original.Spl, original.Sppt, original.Fppt },
                    TestValue = watts,
                    Readback = readbacks,
                    Restored = null,
                    Samples = loaded
                });
                page.Children.Add(matched
                    ? Status($"The power limit read back at {watts} watts four times in a row.")
                    : Warning($"The power limit did not read back as {watts} watts every time."));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                tests.Add(Failed("tdp", "atkacpi", ex.Message));
                page.Children.Add(Warning($"The power limit test stopped: {ex.Message}"));
                return;
            }
        }
    }

    private async Task RunAsusProfileAsync(StackPanel page, LabPowerPlan plan, ILabAtkAcpi acpi, LabAsusState original,
        List<LabPowerTestResult> tests)
    {
        var modeId = plan.Asus!.Mode!.Value;
        foreach (var value in plan.Asus.ModeValues.Where(value => value != original.Mode))
        {
            if (!await PowerUnchangedAsync(page, tests, "power-profile", "atkacpi"))
            {
                break;
            }

            var name = plan.Asus.ModeNames.GetValueOrDefault(value, value.ToString());
            page.Children.Add(Status($"Switching the performance mode to {name}..."));
            try
            {
                await Task.Run(() => acpi.SetVerified(modeId, value));
                tests.Add(new LabPowerTestResult
                {
                    Feature = "power-profile",
                    Transport = "atkacpi",
                    Outcome = "passed",
                    Detail = $"Switched to {name} and read back.",
                    Original = original.Mode,
                    TestValue = value,
                    Readback = value,
                    Restored = null
                });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                tests.Add(Failed("power-profile", "atkacpi", ex.Message));
                page.Children.Add(Warning($"The performance mode test stopped: {ex.Message}"));
                break;
            }
        }

        // Put the mode back before the fan curves, which are read for the current mode.
        try
        {
            await Task.Run(() =>
            {
                if (acpi.Get(modeId) != original.Mode)
                {
                    acpi.SetVerified(modeId, original.Mode);
                }
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            page.Children.Add(Warning($"The performance mode could not be put back yet: {ex.Message}"));
        }
    }

    private async Task RunAsusFanAsync(StackPanel page, LabPowerPlan plan, ILabAtkAcpi acpi, LabAsusState original,
        List<LabPowerTestResult> tests)
    {
        // One fan at a time, each raised by 15 percentage points, as AllyXLab's fan test does.
        foreach (var (cpu, hex, label) in new[]
                 {
                     (true, original.CpuCurve, "CPU fan"),
                     (false, original.GpuCurve, "GPU fan")
                 })
        {
            if (LabPowerPlan.FanTestCurve(Convert.FromHexString(hex)) is null)
            {
                continue;
            }

            if (!await PowerUnchangedAsync(page, tests, "fan", "atkacpi"))
            {
                return;
            }

            page.Children.Add(Status($"Speeding up the {label} for a moment..."));
            try
            {
                var written = await Task.Run(() => acpi.ApplyFanTest(original, cpu));
                page.Children.Add(Status("Did you hear the fan change?"));
                var answer = await AskAsync(page, LouderAnswers);
                tests.Add(new LabPowerTestResult
                {
                    Feature = "fan",
                    Transport = "atkacpi",
                    Outcome = "passed",
                    Detail = $"Raised the {label} duties by 15 points and read the curve back.",
                    Original = cpu ? original.CpuCurve : original.GpuCurve,
                    TestValue = written,
                    Readback = written,
                    Restored = null,
                    TesterAnswer = LouderAnswers[answer]
                });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException
                                           or FormatException)
            {
                tests.Add(Failed("fan", "atkacpi", ex.Message));
                page.Children.Add(Warning($"The fan test stopped: {ex.Message}"));
                return;
            }
        }
    }

    private async Task<bool> RestoreAsusPowerAsync(StackPanel page, ILabAtkAcpi acpi, LabAsusState original,
        List<LabPowerTestResult> tests)
    {
        var restored = await Task.Run(() => acpi.Restore(original));
        await Task.Run(() => _machine.Update(changes => changes with
        {
            Power = restored && changes.Power is not null ? changes.Power with { AsusPower = null } : changes.Power
        }));
        foreach (var test in tests.Where(test => test.Transport == "atkacpi" && test.Feature != "charge-limit")
                     .ToArray())
        {
            tests[tests.IndexOf(test)] = test with { Restored = restored };
        }

        page.Children.Add(restored
            ? Status("The power, performance mode and fan settings were put back.")
            : Warning(
                "The power or fan settings could not be confirmed as put back. Set them again in Armoury Crate, or use the button below."));
        return restored;
    }

    // The charge limit test. The original was recorded by the checkpoint; returns whether nothing is left
    // changed.
    private async Task<bool> RunAsusChargeAsync(StackPanel page, LabPowerPlan plan, ILabAtkAcpi acpi, int? original,
        List<LabPowerTestResult> tests)
    {
        var chargeId = plan.Asus!.Charge!.Value;
        if (original is not { } current)
        {
            page.Children.Add(Muted("The charge limit could not be read, so it was not tested."));
            return true;
        }

        var target = LabPowerPlan.TestChargeLimit(current);
        if (!await PowerUnchangedAsync(page, tests, "charge-limit", "atkacpi"))
        {
            // Nothing was written, so the recorded original is no longer needed.
            await ForgetRecordedAsync(changes => changes with { AsusChargeLimit = null });
            return true;
        }

        page.Children.Add(Status($"Setting the charge limit to {target}%, then putting it back..."));
        bool restored;
        try
        {
            await Task.Run(() => acpi.Set(chargeId, target));
            await Task.Delay(150, Lifetime);
            var readback = await Task.Run(() => acpi.TryGet(chargeId));
            var matched = readback == target;
            restored = await RestoreAsusChargeAsync(acpi, chargeId, current);
            tests.Add(new LabPowerTestResult
            {
                Feature = "charge-limit",
                Transport = "atkacpi",
                Outcome = matched ? "passed" : "failed",
                Detail = matched ? $"Set to {target}% and read back." : $"Read back {readback}%, not {target}%.",
                Original = current,
                TestValue = target,
                Readback = readback,
                Restored = restored
            });
            page.Children.Add(RestoreLine(matched, restored, "charge limit"));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            restored = await RestoreAsusChargeAsync(acpi, chargeId, current);
            tests.Add(Failed("charge-limit", "atkacpi", ex.Message) with { Restored = restored });
            page.Children.Add(Warning($"The charge limit test stopped: {ex.Message}"));
        }

        return restored;
    }

    private async Task<bool> RestoreAsusChargeAsync(ILabAtkAcpi acpi, uint chargeId, int original)
    {
        var restored = await Task.Run(() =>
        {
            try
            {
                if (acpi.TryGet(chargeId) != original)
                {
                    acpi.Set(chargeId, original);
                    Thread.Sleep(150);
                }

                return acpi.TryGet(chargeId) == original;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                return false;
            }
        });
        if (restored)
        {
            await Task.Run(() => _machine.Update(changes => changes with
            {
                Power = changes.Power is null ? null : changes.Power with { AsusChargeLimit = null }
            }));
        }

        return restored;
    }

    private async Task RunMsiTestsAsync(LabProject project, string attempt, StackPanel page, LabPowerPlan plan,
        LabPowerLog log, List<LabPowerTestResult> tests, List<LabPowerSample> telemetry, string passLabel)
    {
        var layout = plan.Msi!;
        if (!layout.HasTdp && layout.Charge is null)
        {
            return;
        }

        var worker = await WorkerAsync();
        ILabMsiWmi wmi;
        try
        {
            wmi = await Task.Run(() => worker.Open<ILabMsiWmi>(LabMsiWmi.Service.Name, log, layout));
        }
        catch (Exception ex) when (LabMsiWmi.IsTransportFailure(ex))
        {
            page.Children.Add(Status($"The MSI power interface did not open, so its tests were skipped: {ex.Message}"));
            return;
        }

        using (wmi)
        {
            // The checkpoint: the worker captures the limits, scenario and charge limit itself and accepts no
            // write until they are recorded here and in the project evidence.
            LabMsiOriginal original;
            string token;
            try
            {
                (original, token) = await Task.Run(() => worker.Checkpoint<LabMsiOriginal>(wmi, captured =>
                {
                    LabPowerRecovery.Record(_machine, plan.Record!.Id, changes => changes with
                    {
                        AcLine = _pinnedAcLine,
                        MsiPower = captured.Power ?? changes.MsiPower,
                        MsiChargeRaw = captured.ChargeRaw ?? changes.MsiChargeRaw
                    });
                    WriteEvidenceOnce(project, attempt, $"original-msi-{passLabel}",
                        new { _pinnedAcLine, State = captured });
                }));
            }
            catch (Exception ex) when (LabMsiWmi.IsTransportFailure(ex))
            {
                page.Children.Add(
                    Warning($"The current settings could not be recorded, so nothing was changed: {ex.Message}"));
                return;
            }

            var restored = true;
            if (layout.HasTdp)
            {
                if (original.Power is { } power)
                {
                    restored &= await RunMsiTdpAsync(page, plan, wmi, power, tests, telemetry, passLabel);
                }
                else
                {
                    page.Children.Add(Status(
                        $"The current power limits could not be read, so the test was skipped: {original.PowerUnavailable}"));
                }
            }

            if (layout.Charge is not null)
            {
                restored &= await RunMsiChargeAsync(page, wmi, original, tests);
            }

            if (restored)
            {
                await ReleaseQuietlyAsync(worker, wmi, token);
            }
        }
    }

    // The TDP test from the checkpoint's original; returns whether the limits were put back.
    private async Task<bool> RunMsiTdpAsync(StackPanel page, LabPowerPlan plan, ILabMsiWmi wmi, LabMsiState original,
        List<LabPowerTestResult> tests, List<LabPowerSample> telemetry, string passLabel)
    {
        var failed = false;
        foreach (var watts in LabPowerPlan.TestWattsList(plan.Msi!.MinimumWatts, plan.Msi.MaximumWatts))
        {
            if (!await PowerUnchangedAsync(page, tests, "tdp", "wmi-method"))
            {
                failed = true;
                break;
            }

            page.Children.Add(Status($"Setting the power limit to {watts} watts for ten seconds..."));
            try
            {
                await Task.Run(() => wmi.WritePair(original, watts, watts));
                var (matched, readbacks) = await ReadbackSamplesAsync(() =>
                {
                    var readback = wmi.ReadPower();
                    return (new { readback.Sustained, readback.Boost, Fans = wmi.FanSpeeds() },
                        readback.Sustained == watts && readback.Boost == watts);
                });
                var loaded = await LabPowerLoad.RunAsync(LoadTest, SampleInterval,
                    label => ReadTelemetry($"tdp-{watts}w-{passLabel}-{label}", plan), null, Lifetime);
                telemetry.AddRange(loaded);
                tests.Add(new LabPowerTestResult
                {
                    Feature = "tdp",
                    Transport = "wmi-method",
                    Outcome = matched ? "applied-readback-matched" : "readback-mismatch",
                    Detail = $"{SourceName(passLabel)}: set {watts} W; four readbacks 1 s apart "
                             + (matched ? "all matched." : "did not all match."),
                    Original = new { original.Sustained, original.Boost },
                    TestValue = watts,
                    Readback = readbacks,
                    Restored = null,
                    Samples = loaded
                });
                page.Children.Add(matched
                    ? Status($"The power limit read back at {watts} watts four times in a row.")
                    : Warning($"The power limit did not read back as {watts} watts every time."));
            }
            catch (Exception ex) when (LabMsiWmi.IsTransportFailure(ex))
            {
                tests.Add(Failed("tdp", "wmi-method", ex.Message));
                page.Children.Add(Warning($"The power limit test stopped: {ex.Message}"));
                failed = true;
                break;
            }
        }

        var restored = await RestoreMsiPowerAsync(wmi, original);
        foreach (var test in tests.Where(test => test is { Transport: "wmi-method", Feature: "tdp", Restored: null })
                     .ToArray())
        {
            tests[tests.IndexOf(test)] = test with { Restored = restored };
        }

        page.Children.Add(RestoreLine(!failed, restored, "power limit"));
        return restored;
    }

    private async Task<bool> RestoreMsiPowerAsync(ILabMsiWmi wmi, LabMsiState original)
    {
        var restored = await Task.Run(() => wmi.RestorePower(original));
        if (restored)
        {
            await Task.Run(() => _machine.Update(changes => changes with
            {
                Power = changes.Power is null ? null : changes.Power with { MsiPower = null }
            }));
        }

        return restored;
    }

    // The charge limit test. The original was recorded by the checkpoint; returns whether nothing is left
    // changed.
    private async Task<bool> RunMsiChargeAsync(StackPanel page, ILabMsiWmi wmi, LabMsiOriginal checkpoint,
        List<LabPowerTestResult> tests)
    {
        if (checkpoint.ChargeRaw is not { } originalRaw)
        {
            page.Children.Add(
                Muted($"The charge limit could not be read, so it was not tested: {checkpoint.ChargeUnavailable}"));
            return true;
        }

        var currentPercent = originalRaw & LabMsiWmi.ChargePercentMask;
        var target = LabPowerPlan.TestChargeLimit(currentPercent);
        if (!await PowerUnchangedAsync(page, tests, "charge-limit", "wmi-method"))
        {
            // Nothing was written, so the recorded original is no longer needed.
            await ForgetRecordedAsync(changes => changes with { MsiChargeRaw = null });
            return true;
        }

        page.Children.Add(Status($"Setting the charge limit to {target}%, then putting it back..."));
        bool restored;
        try
        {
            await Task.Run(() => wmi.WriteChargeRaw(LabMsiWmi.EncodeCharge(originalRaw, target)));
            await Task.Delay(150, Lifetime);
            var readbackRaw = await Task.Run(wmi.ReadChargeRaw);
            var matched = (readbackRaw & LabMsiWmi.ChargePercentMask) == target;
            restored = await RestoreMsiChargeAsync(wmi, originalRaw);
            tests.Add(new LabPowerTestResult
            {
                Feature = "charge-limit",
                Transport = "wmi-method",
                Outcome = matched ? "passed" : "failed",
                Detail = matched
                    ? $"Set to {target}% and read back."
                    : $"Read back {readbackRaw & LabMsiWmi.ChargePercentMask}%.",
                Original = currentPercent,
                TestValue = target,
                Readback = readbackRaw & LabMsiWmi.ChargePercentMask,
                Restored = restored
            });
            page.Children.Add(RestoreLine(matched, restored, "charge limit"));
        }
        catch (Exception ex) when (LabMsiWmi.IsTransportFailure(ex))
        {
            restored = await RestoreMsiChargeAsync(wmi, originalRaw);
            tests.Add(Failed("charge-limit", "wmi-method", ex.Message) with { Restored = restored });
            page.Children.Add(Warning($"The charge limit test stopped: {ex.Message}"));
        }

        return restored;
    }

    private async Task<bool> RestoreMsiChargeAsync(ILabMsiWmi wmi, int originalRaw)
    {
        var restored = await Task.Run(() =>
        {
            try
            {
                if (wmi.ReadChargeRaw() != originalRaw)
                {
                    wmi.WriteChargeRaw(originalRaw);
                }

                return wmi.ReadChargeRaw() == originalRaw;
            }
            catch (Exception ex) when (LabMsiWmi.IsTransportFailure(ex))
            {
                return false;
            }
        });
        if (restored)
        {
            await Task.Run(() => _machine.Update(changes => changes with
            {
                Power = changes.Power is null ? null : changes.Power with { MsiChargeRaw = null }
            }));
        }

        return restored;
    }

    private async Task<bool> RunLightingAsync(StackPanel page, LabPowerPlan plan, LabPowerLog log,
        List<(string Shown, string Seen)> lighting)
    {
        page.Children.Add(Heading("Lighting"));
        var found = await RunLampArrayAsync(page, log, lighting);
        found |= await RunAuraAsync(page, plan, log, lighting);
        if (!found)
        {
            page.Children.Add(Status("No controllable lighting was found on this device."));
        }

        return found;
    }

    private async Task<bool> RunLampArrayAsync(StackPanel page, LabPowerLog log,
        List<(string Shown, string Seen)> lighting)
    {
        LabLampArrays arrays;
        try
        {
            arrays = await LabLampArrays.OpenAsync(Lifetime);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            page.Children.Add(Muted($"Windows Dynamic Lighting could not be read: {ex.Message}"));
            return false;
        }

        using (arrays)
        {
            foreach (var problem in arrays.Problems)
            {
                log.Add("lamparray-problem", problem);
            }

            if (arrays.Count == 0)
            {
                return false;
            }

            for (var index = 0; index < arrays.Count; index++)
            {
                var info = await Task.Run(() => arrays.Describe(index));
                log.Add("lamparray", info);
                page.Children.Add(Status(
                    $"Found \"{info.Name}\" ({info.Kind}, {info.LampCount} lamps). Watch it change colour."));
                if (!info.IsEnabled)
                {
                    page.Children.Add(Muted(
                        "Windows reports this light as not available to apps right now; the colours may not change."));
                }

                await ShowLampColoursAsync(page, arrays, index, info.Name, lighting);
            }

            // Disposing releases the arrays back to Windows; no restore write is needed.
            return true;
        }
    }

    private async Task ShowLampColoursAsync(StackPanel page, LabLampArrays arrays, int index, string name,
        List<(string Shown, string Seen)> lighting)
    {
        (string Shown, byte R, byte G, byte B)[] steps =
        [
            ("red", 255, 0, 0),
            ("green", 0, 255, 0),
            ("blue", 0, 0, 255),
            ("off", 0, 0, 0)
        ];
        foreach (var (shown, r, g, b) in steps)
        {
            try
            {
                await Task.Run(() => arrays.SetAll(index, r, g, b));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                page.Children.Add(Muted($"The colour could not be set on \"{name}\": {ex.Message}"));
                return;
            }

            page.Children.Add(Status($"Showing {shown}. Which colour do you see?"));
            var answer = await AskAsync(page, SeenColours);
            lighting.Add(($"lamparray:{name}:{shown}", SeenColours[answer].ToLowerInvariant()));
        }
    }

    private async Task<bool> RunAuraAsync(StackPanel page, LabPowerPlan plan, LabPowerLog log,
        List<(string Shown, string Seen)> lighting)
    {
        if (plan.Aura is null || !_options.Elevated)
        {
            return false;
        }

        var worker = await WorkerAsync();
        ILabAura aura;
        try
        {
            aura = await Task.Run(() => worker.Open<ILabAura>(LabAuraLighting.Service.Name, log, plan.Aura!));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Win32Exception)
        {
            log.Add("aura-open-failed", ex.Message);
            page.Children.Add(Muted("The device's own lighting interface was not found, so it was not tested."));
            return false;
        }

        using (aura)
        {
            // Lighting prep: the tester turns the lights off first so a flash is obvious, and on the Xbox
            // Ally X also turns off Windows Dynamic Lighting so Windows does not repaint the lights.
            var xbox = plan.Record!.Id.Contains("xbox", StringComparison.OrdinalIgnoreCase);
            page.Children.Add(Status(
                "This device's lights are write-only, so the colour cannot be read. "
                + "Turn the lights off in Armoury Crate first"
                + (xbox
                    ? ", and turn off Dynamic Lighting in Windows Settings under Personalization, "
                    : ", ")
                + "then press Ready. Set your colour again in Armoury Crate when the test is done."));
            var prep = await AskAsync(page, "Ready", "Skip lighting");
            if (prep == 1)
            {
                return false;
            }

            // The checkpoint: the lights cannot be read, so it only records that the lab is about to write
            // them; the tester restores their colour. It is never released, because nothing can verify a
            // restore, and it ends when the session closes.
            try
            {
                await Task.Run(() => worker.Checkpoint<LabAuraOriginal>(aura, captured =>
                {
                    LabPowerRecovery.Record(_machine, plan.Record.Id,
                        changes => changes with { AuraWrittenAt = DateTimeOffset.UtcNow });
                    log.Add("aura-checkpoint", captured);
                }));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                page.Children.Add(Warning($"The lighting test did not start: {ex.Message}"));
                return true;
            }

            string[] zones =
            [
                "both rings", "left ring outer half", "left ring inner half", "right ring inner half",
                "right ring outer half"
            ];
            string[] colours = ["red", "green", "blue"];
            var stopped = false;
            try
            {
                for (var zone = 0; zone < zones.Length && !stopped; zone++)
                {
                    for (var channel = 0; channel < colours.Length; channel++)
                    {
                        var shown = $"{colours[channel]} on the {zones[zone]}";
                        try
                        {
                            await Task.Run(() => aura.Colour(zone, channel));
                        }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException)
                        {
                            page.Children.Add(Warning($"The lighting write stopped: {ex.Message}"));
                            stopped = true;
                            break;
                        }

                        // Hold the colour for two seconds, as AllyXLab does, then ask.
                        await Task.Delay(2000, Lifetime);
                        page.Children.Add(Status($"Showing {shown}. Did the light match?"));
                        var answer = await AskAsync(page, "Matched", "Different", "Still on / stop");
                        lighting.Add(($"{colours[channel]}:{zones[zone]}", AuraAnswer(answer)));
                        if (answer == 2)
                        {
                            stopped = true;
                            break;
                        }
                    }
                }
            }
            finally
            {
                // Brightness to 0, whatever happened, before the interface is released.
                try
                {
                    await Task.Run(aura.Off);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    log.Add("aura-off-error", ex.Message);
                }
            }

            // Write-only lighting cannot be read back, so the tester restores their colour in Armoury
            // Crate; clear the note now that they have been told.
            await Task.Run(() => LabPowerRecovery.ClearAura(_machine));
            page.Children.Add(Status("Remember to set your colour again in Armoury Crate."));
            return true;
        }
    }

    private static string AuraAnswer(int index)
    {
        return index switch
        {
            0 => "matched",
            1 => "different",
            _ => "still on"
        };
    }

    private async Task RestorePendingAsync(StackPanel page, List<LabPowerTestResult> tests)
    {
        if (!LabPowerChanges.PowerPending(_machine.Read().Power))
        {
            return;
        }

        var worker = await WorkerAsync();
        var outcome = await Task.Run(() => LabPowerRecovery.RestorePower(_machine, worker, new LabPowerLog()));
        if (outcome.Restored)
        {
            return;
        }

        // An unverified restore: orange, with plain instructions and an explicit retry button. No
        // write is retried automatically.
        page.Children.Add(Warning(
            "Some settings could not be confirmed as put back: " + outcome.Message
                                                                 + " You can set them again in the device's own app, or press the button to try once more."));
        Button retry = new() { Content = "Try restoring again" };
        retry.Click += (_, _) => Run(page, async () =>
        {
            retry.IsEnabled = false;
            var again = await Task.Run(() => LabPowerRecovery.RestorePower(_machine, worker, new LabPowerLog()));
            page.Children.Add(again.Restored
                ? Status("The settings were put back.")
                : Warning(again.Message));
            retry.IsEnabled = !again.Restored;
        });
        page.Children.Add(Buttons(retry));
    }

    // Called immediately before every device write: the charger state is unchanged and the machine is
    // still the confirmed device with the BIOS, EC and vendor endpoints captured at the start.
    private async Task<bool> PowerUnchangedAsync(StackPanel page, List<LabPowerTestResult> tests, string feature,
        string transport)
    {
        if (!await Task.Run(() => LabPowerGate.PowerSourceUnchanged(_pinnedAcLine)))
        {
            tests.Add(Failed(feature, transport,
                "The charger was plugged in or unplugged during the test, so it stopped."));
            page.Children.Add(
                Warning("The charger state changed during the test, so it stopped before the next step."));
            return false;
        }

        if (_powerRecord is { } record && !await Task.Run(() => LabPowerIdentity.Matches(record)))
        {
            tests.Add(Failed(feature, transport,
                "The BIOS, embedded controller or controller firmware changed during the test, so it stopped."));
            page.Children.Add(Warning(
                "The device's firmware or controller changed during the test, so it stopped before the next step."));
            return false;
        }

        return true;
    }

    // After a TDP write: four readbacks one second apart, each with the charger state and the fan
    // readings the transport returns. The write counts as applied only when all four match and the
    // charger state never changed; one failed read is a mismatch, and nothing is written again.
    private async Task<(bool Matched, IReadOnlyList<object> Samples)> ReadbackSamplesAsync(
        Func<(object Values, bool Matches)> read)
    {
        List<object> samples = [];
        var matched = true;
        for (var i = 1; i <= 4; i++)
        {
            var index = i;
            await Task.Delay(1000, Lifetime);
            var (record, ok) = await Task.Run(() =>
            {
                var ac = LabPowerTelemetry.AcLine();
                try
                {
                    var (values, matches) = read();
                    var same = matches && ac == _pinnedAcLine;
                    return (new
                    {
                        Sample = index,
                        At = DateTimeOffset.UtcNow,
                        AcLine = ac,
                        Values = values,
                        Matches = same
                    }, same);
                }
                catch (Exception ex) when (ex is Win32Exception || LabMsiWmi.IsTransportFailure(ex))
                {
                    return ((object)new
                    {
                        Sample = index,
                        At = DateTimeOffset.UtcNow,
                        AcLine = ac,
                        Problem = ex.Message,
                        Matches = false
                    }, false);
                }
            });
            samples.Add(record);
            matched &= ok;
        }

        return (matched, samples);
    }

    // Clears a recorded original that no write followed.
    private Task ForgetRecordedAsync(Func<LabPowerChanges, LabPowerChanges> clear)
    {
        return Task.Run(() => _machine.Update(changes => changes with
        {
            Power = changes.Power is null ? null : clear(changes.Power)
        }));
    }

    private static void WriteContext(LabProject project, string attempt, string name, LabPowerContext context)
    {
        WriteEvidenceOnce(project, attempt, name, context);
    }

    // Writes one evidence file, ignoring a repeat name so a second pass or a retry does not throw.
    private static void WriteEvidenceOnce<T>(LabProject project, string attempt, string name, T value)
    {
        try
        {
            project.WriteEvidence(attempt, name, value);
        }
        catch (IOException)
        {
            // The file already exists from an earlier pass; the first write is the one that matters.
        }
    }

    private static string SourceName(string passLabel)
    {
        return passLabel switch
        {
            "ac" => "on AC power",
            "battery" => "on battery",
            _ => "on the current power source"
        };
    }

    private static LabPowerTestResult Failed(string feature, string transport, string detail)
    {
        return new LabPowerTestResult
        {
            Feature = feature,
            Transport = transport,
            Outcome = "failed",
            Detail = detail,
            Restored = null
        };
    }

    private static TextBlock RestoreLine(bool matched, bool restored, string what)
    {
        return !restored
            ? Warning(
                $"The {what} could not be confirmed as put back. Set it again in the device's own app, or use the button below.")
            : matched
                ? Status($"The {what} was tested and put back.")
                : Warning($"The {what} did not read back as the test value, but it was put back.");
    }
}
