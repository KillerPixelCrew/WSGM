using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using LibreHardwareMonitor.PawnIo;
using WSGM.DeviceLab.Transports;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Gui;

// The processor power-limit test for a machine with no curated interface: one bounded, lower limit
// through the AMD SMU (PawnIO RyzenSMU) or Intel's MCHBAR and MSR (KX), read back from a real source,
// held under load, and put back. The original is recorded before the write, so a killed session is
// undone on the next start. Nothing is retried; the EC is never touched.
internal sealed partial class WizardWindow
{
    private async Task RunProcessorPowerAsync(LabProject project, string attempt, StackPanel page,
        LabPowerPlan plan, List<LabPowerTestResult> tests, List<LabPowerSample> telemetry, string passLabel)
    {
        if (LabPowerChanges.PowerPending(_machine.Read().Power))
        {
            page.Children.Add(Warning(
                "An earlier test could not confirm it put the settings back, so no further power test runs until they are restored."));
            await RestorePendingAsync(page, tests);
            if (LabPowerChanges.PowerPending(_machine.Read().Power))
            {
                return;
            }
        }

        var gate = await Task.Run(LabPowerGate.CheckProcessor);
        await Task.Run(() => project.WriteEvidence(attempt, $"context-processor-{passLabel}", gate.Context));
        if (!gate.Ok)
        {
            page.Children.Add(Warning(gate.Problem ?? "The machine is not ready for a power test."));
            return;
        }

        _pinnedAcLine = gate.AcLine;
        switch (CpuVendor())
        {
            case "AuthenticAMD":
                await RunAmdPowerAsync(project, attempt, page, plan, tests, telemetry, passLabel);
                break;
            case "GenuineIntel":
                await RunIntelPowerAsync(project, attempt, page, plan, tests, telemetry, passLabel);
                break;
            default:
                page.Children.Add(Muted("This processor has no power-limit test in this build."));
                break;
        }
    }

    private async Task RunAmdPowerAsync(LabProject project, string attempt, StackPanel page, LabPowerPlan plan,
        List<LabPowerTestResult> tests, List<LabPowerSample> telemetry, string passLabel)
    {
        if (!LabPawnIoModule.RyzenSmuBundled)
        {
            tests.Add(Failed("processor-power", "ryzen-smu", "unavailable: this build has no RyzenSMU module"));
            page.Children.Add(Muted("This build does not include the RyzenSMU module, so the processor power limit was not tested."));
            return;
        }

        LabAmdSmu smu;
        try
        {
            smu = await Task.Run(LabAmdSmu.Open);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            tests.Add(Failed("processor-power", "ryzen-smu", $"unavailable: PawnIO ({ex.Message})"));
            page.Children.Add(Muted($"The processor power limit could not be reached through PawnIO: {ex.Message}"));
            return;
        }

        using (smu)
        {
            if (smu.Commands is null)
            {
                tests.Add(Failed("processor-power", "ryzen-smu", $"{smu.CodeNameText} is not a supported mobile processor"));
                page.Children.Add(Muted($"{smu.CodeNameText} has no known power-limit commands, so it was not tested."));
                return;
            }

            // Two readings a moment apart must agree before anything is written.
            LabAmdLimits original;
            try
            {
                original = await Task.Run(smu.ReadLimits);
                await Task.Delay(200, Lifetime);
                var again = await Task.Run(smu.ReadLimits);
                if (!LabPowerRecovery.Same(original, again) || !LabAmdSmu.Plausible(original))
                {
                    tests.Add(Failed("processor-power", "ryzen-smu",
                        $"the PM table did not give stable, plausible limits ({original.Stapm}/{original.Fast}/{original.Slow} W)"));
                    page.Children.Add(Muted("The current processor power limits could not be read reliably, so nothing was changed."));
                    return;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
            {
                tests.Add(Failed("processor-power", "ryzen-smu", ex.Message));
                page.Children.Add(Muted($"The processor power limits could not be read: {ex.Message}"));
                return;
            }

            var testStapm = Math.Max(LabAmdSmu.MinimumTestWatts, Math.Round(original.Stapm * 0.75));
            LabAmdLimits target = new(testStapm, Math.Max(testStapm, Math.Round(original.Fast * 0.75)), testStapm);
            await Task.Run(() =>
            {
                LabPowerRecovery.Record(_machine, LabPowerChanges.ProcessorRecordId,
                    changes => changes with { AcLine = _pinnedAcLine, AmdLimits = original });
                project.WriteEvidence(attempt, $"original-amd-{passLabel}",
                    new { _pinnedAcLine, smu.CodeNameText, SmuVersion = $"0x{smu.SmuVersion:X8}", Limits = original });
            });

            page.Children.Add(Status(
                $"Lowering the processor power limit from {original.Stapm} to {target.Stapm} watts for ten seconds, then putting it back. The device may feel slower meanwhile."));
            LabPowerTestResult? result = null;
            try
            {
                var responses = await Task.Run(() => smu.WriteLimits(target));
                var samples = await LabPowerLoad.RunAsync(LoadTest, SampleInterval,
                    label => LabPowerTelemetry.Read($"processor-{target.Stapm}w-{passLabel}-{label}", FanReader(plan)), null,
                    Lifetime);
                telemetry.AddRange(samples);
                List<LabAmdLimits> readbacks = [];
                for (var i = 0; i < 4; i++)
                {
                    readbacks.Add(await Task.Run(smu.ReadLimits));
                    await Task.Delay(1000, Lifetime);
                }

                var matched = readbacks.All(readback => LabPowerRecovery.Same(readback, target));
                result = new LabPowerTestResult
                {
                    Feature = "processor-power",
                    Transport = "ryzen-smu",
                    Outcome = matched ? "applied-readback-matched" : "readback-mismatch",
                    Detail = $"{SourceName(passLabel)}: {smu.CodeNameText}, set {target.Stapm}/{target.Fast}/{target.Slow} W, SMU echoed {string.Join("/", responses)} mW.",
                    Original = original,
                    TestValue = target,
                    Readback = readbacks,
                    Samples = samples
                };
                page.Children.Add(matched
                    ? Status($"The processor ran at the lower limit ({target.Stapm} watts).")
                    : Warning($"The processor did not report the lower limit: {readbacks[^1].Stapm} watts."));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException
                                           or ArgumentOutOfRangeException)
            {
                result = Failed("processor-power", "ryzen-smu", ex.Message);
                page.Children.Add(Warning($"The processor power test stopped: {ex.Message}"));
            }
            finally
            {
                // Restore without the stage token, so "Stop and save" still puts the limit back.
                var outcome = await Task.Run(() => LabPowerRecovery.RestorePower(_machine, new LabPowerLog()));
                page.Children.Add(RestoreLine(true, outcome.Restored, "processor power limit"));
                if (!outcome.Restored)
                {
                    page.Children.Add(Warning(outcome.Message + " Restart the device to reset the processor power limit."));
                }

                tests.Add((result ?? Failed("processor-power", "ryzen-smu", "stopped")) with { Restored = outcome.Restored });
            }
        }
    }

    private async Task RunIntelPowerAsync(LabProject project, string attempt, StackPanel page, LabPowerPlan plan,
        List<LabPowerTestResult> tests, List<LabPowerSample> telemetry, string passLabel)
    {
        double unit;
        LabIntelKx kx;
        try
        {
            unit = await Task.Run(PowerUnitWatts);
            kx = await Task.Run(LabIntelKx.Open);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                       or Win32Exception)
        {
            tests.Add(Failed("processor-power", "kx", $"unavailable: {ex.Message}"));
            page.Children.Add(Muted($"The processor power limit could not be reached: {ex.Message}"));
            return;
        }

        using (kx)
        {
            LabIntelLimits original;
            try
            {
                original = await Task.Run(() => kx.Read(unit));
                var again = await Task.Run(() => kx.Read(unit));
                if (again != original || original.MchbarPl1Watts is < 3 or > 150)
                {
                    tests.Add(Failed("processor-power", "kx",
                        $"the limits were not stable or plausible (MCHBAR PL1 {original.MchbarPl1Watts} W, MSR PL1 {original.MsrPl1Watts} W)"));
                    page.Children.Add(Muted("The current processor power limits could not be read reliably, so nothing was changed."));
                    return;
                }
            }
            catch (InvalidOperationException ex)
            {
                tests.Add(Failed("processor-power", "kx", ex.Message));
                page.Children.Add(Muted($"The processor power limits could not be read: {ex.Message}"));
                return;
            }

            var target = Math.Max(5, Math.Round(original.MchbarPl1Watts * 0.75));
            await Task.Run(() =>
            {
                LabPowerRecovery.Record(_machine, LabPowerChanges.ProcessorRecordId,
                    changes => changes with { AcLine = _pinnedAcLine, IntelLimits = original });
                project.WriteEvidence(attempt, $"original-intel-{passLabel}", new { _pinnedAcLine, Limits = original });
            });

            page.Children.Add(Status(
                $"Lowering the processor power limit from {original.MchbarPl1Watts} to {target} watts for ten seconds, then putting it back."));
            LabPowerTestResult? result = null;
            try
            {
                var problem = await Task.Run(() => kx.WritePl1(original, target));
                var samples = await LabPowerLoad.RunAsync(LoadTest, SampleInterval,
                    label => LabPowerTelemetry.Read($"processor-{target}w-{passLabel}-{label}", FanReader(plan)), null, Lifetime);
                telemetry.AddRange(samples);
                List<LabIntelLimits> readbacks = [];
                for (var i = 0; i < 4; i++)
                {
                    readbacks.Add(await Task.Run(() => kx.Read(unit)));
                    await Task.Delay(1000, Lifetime);
                }

                var matched = problem is null
                              && readbacks.All(readback => Math.Abs(readback.MchbarPl1Watts - target) < 0.5
                                                           && (original.MsrLocked || Math.Abs(readback.MsrPl1Watts - target) < 0.5));
                result = new LabPowerTestResult
                {
                    Feature = "processor-power",
                    Transport = "kx",
                    Outcome = matched ? "applied-readback-matched" : "readback-mismatch",
                    Detail = $"{SourceName(passLabel)}: set PL1 {target} W{(original.MsrLocked ? " (MSR 0x610 is locked, so only the MCHBAR mirror was written)" : string.Empty)}. {problem}",
                    Original = original,
                    TestValue = target,
                    Readback = readbacks,
                    Samples = samples
                };
                page.Children.Add(matched
                    ? Status($"The processor limit read back at {target} watts.")
                    : Warning($"The processor limit did not read back at {target} watts."));
            }
            catch (InvalidOperationException ex)
            {
                result = Failed("processor-power", "kx", ex.Message);
                page.Children.Add(Warning($"The processor power test stopped: {ex.Message}"));
            }
            finally
            {
                var outcome = await Task.Run(() => LabPowerRecovery.RestorePower(_machine, new LabPowerLog()));
                page.Children.Add(RestoreLine(true, outcome.Restored, "processor power limit"));
                if (!outcome.Restored)
                {
                    page.Children.Add(Warning(outcome.Message + " Restart the device to reset the processor power limit."));
                }

                tests.Add((result ?? Failed("processor-power", "kx", "stopped")) with { Restored = outcome.Restored });
                await Task.Run(() => project.WriteEvidence(attempt, $"kx-log-{passLabel}", kx.Log));
            }
        }
    }

    private static string CpuVendor()
    {
        if (!X86Base.IsSupported)
        {
            return string.Empty;
        }

        var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        var bytes = new byte[12];
        BitConverter.GetBytes(ebx).CopyTo(bytes, 0);
        BitConverter.GetBytes(edx).CopyTo(bytes, 4);
        BitConverter.GetBytes(ecx).CopyTo(bytes, 8);
        return Encoding.ASCII.GetString(bytes);
    }

    // MSR 0x606 bits 0-3: power unit is 1 / 2^n watts (usually n = 3, an eighth of a watt).
    private static double PowerUnitWatts()
    {
        var msr = new IntelMsr();
        try
        {
            return msr.ReadMsr(0x606, out ulong value)
                ? 1.0 / (1 << (int)(value & 0xF))
                : throw new InvalidOperationException("MSR 0x606 could not be read through PawnIO.");
        }
        finally
        {
            msr.Close();
        }
    }
}
