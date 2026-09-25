using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using WSGM.DeviceLab.Transports;
using WSGM.DeviceLab.Wizard;
using WSGM.DeviceLab.Worker;

namespace WSGM.DeviceLab.Gui;

// The processor power-limit test for a machine with no curated interface: one bounded, lower limit
// through the AMD SMU (PawnIO RyzenSMU) or Intel's MCHBAR and MSR (KX), read back from a real source,
// held under load, and put back. Both transports run in the hardware worker: its checkpoint captures the
// original, which is recorded before the worker accepts the write, so a killed session is undone on the
// next start. Nothing is retried; the EC is never touched.
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

        var worker = await WorkerAsync();
        ILabAmdSmu smu;
        LabAmdIdentity identity;
        try
        {
            smu = await Task.Run(() => worker.Open<ILabAmdSmu>(LabAmdSmu.Service.Name, null));
            identity = await Task.Run(smu.Identity);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            tests.Add(Failed("processor-power", "ryzen-smu", $"unavailable: PawnIO ({ex.Message})"));
            page.Children.Add(Muted($"The processor power limit could not be reached through PawnIO: {ex.Message}"));
            return;
        }

        using (smu)
        {
            if (!identity.Supported)
            {
                tests.Add(Failed("processor-power", "ryzen-smu", $"{identity.CodeNameText} is not a supported mobile processor"));
                page.Children.Add(Muted($"{identity.CodeNameText} has no known power-limit commands, so it was not tested."));
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

            // The checkpoint: the worker reads the limits itself, and accepts no write until that reading
            // (which must still agree with the two above) is recorded.
            string token;
            try
            {
                var stable = original;
                (original, token) = await Task.Run(() => worker.Checkpoint<LabAmdLimits>(smu, captured =>
                {
                    if (!LabPowerRecovery.Same(captured, stable))
                    {
                        throw new InvalidOperationException("The processor power limits changed while they were recorded.");
                    }

                    LabPowerRecovery.Record(_machine, LabPowerChanges.ProcessorRecordId,
                        changes => changes with { AcLine = _pinnedAcLine, AmdLimits = captured });
                    project.WriteEvidence(attempt, $"original-amd-{passLabel}",
                        new { _pinnedAcLine, identity.CodeNameText, SmuVersion = $"0x{identity.SmuVersion:X8}", Limits = captured });
                }));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException or IOException)
            {
                tests.Add(Failed("processor-power", "ryzen-smu", ex.Message));
                page.Children.Add(Muted($"The current processor power limits could not be recorded, so nothing was changed: {ex.Message}"));
                return;
            }

            var testStapm = Math.Max(LabAmdSmu.MinimumTestWatts, Math.Round(original.Stapm * 0.75));
            LabAmdLimits target = new(testStapm, Math.Max(testStapm, Math.Round(original.Fast * 0.75)), testStapm);
            page.Children.Add(Status(
                $"Lowering the processor power limit from {original.Stapm} to {target.Stapm} watts for ten seconds, then putting it back. The device may feel slower meanwhile."));
            LabPowerTestResult? result = null;
            try
            {
                var responses = await Task.Run(() => smu.WriteLimits(target));
                var samples = await LabPowerLoad.RunAsync(LoadTest, SampleInterval,
                    label => ReadTelemetry($"processor-{target.Stapm}w-{passLabel}-{label}", plan), null,
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
                    Detail = $"{SourceName(passLabel)}: {identity.CodeNameText}, set {target.Stapm}/{target.Fast}/{target.Slow} W, SMU echoed {string.Join("/", responses)} mW.",
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
                // Restore without the stage token, so "Stop and save" still puts the limit back. The restore
                // opens its own worker session and checkpoint; this one is released once it verified.
                var outcome = await Task.Run(() => LabPowerRecovery.RestorePower(_machine, worker, new LabPowerLog()));
                page.Children.Add(RestoreLine(true, outcome.Restored, "processor power limit"));
                if (!outcome.Restored)
                {
                    page.Children.Add(Warning(outcome.Message + " Restart the device to reset the processor power limit."));
                }
                else
                {
                    await ReleaseQuietlyAsync(worker, smu, token);
                }

                tests.Add((result ?? Failed("processor-power", "ryzen-smu", "stopped")) with { Restored = outcome.Restored });
            }
        }
    }

    private async Task RunIntelPowerAsync(LabProject project, string attempt, StackPanel page, LabPowerPlan plan,
        List<LabPowerTestResult> tests, List<LabPowerSample> telemetry, string passLabel)
    {
        var worker = await WorkerAsync();
        ILabIntelKx kx;
        try
        {
            // The worker reads the power unit from MSR 0x606 itself.
            kx = await Task.Run(() => worker.Open<ILabIntelKx>(LabIntelKx.Service.Name, null, (double?)null));
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
                original = await Task.Run(kx.Read);
                var again = await Task.Run(kx.Read);
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

            // The checkpoint: the worker reads the limits itself, and accepts no write until that reading
            // (which must still equal the two above) is recorded.
            string token;
            try
            {
                var stable = original;
                (original, token) = await Task.Run(() => worker.Checkpoint<LabIntelLimits>(kx, captured =>
                {
                    if (captured != stable)
                    {
                        throw new InvalidOperationException("The processor power limits changed while they were recorded.");
                    }

                    LabPowerRecovery.Record(_machine, LabPowerChanges.ProcessorRecordId,
                        changes => changes with { AcLine = _pinnedAcLine, IntelLimits = captured });
                    project.WriteEvidence(attempt, $"original-intel-{passLabel}", new { _pinnedAcLine, Limits = captured });
                }));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                tests.Add(Failed("processor-power", "kx", ex.Message));
                page.Children.Add(Muted($"The current processor power limits could not be recorded, so nothing was changed: {ex.Message}"));
                return;
            }

            var target = Math.Max(5, Math.Round(original.MchbarPl1Watts * 0.75));
            page.Children.Add(Status(
                $"Lowering the processor power limit from {original.MchbarPl1Watts} to {target} watts for ten seconds, then putting it back."));
            LabPowerTestResult? result = null;
            try
            {
                var problem = await Task.Run(() => kx.WritePl1(original, target));
                var samples = await LabPowerLoad.RunAsync(LoadTest, SampleInterval,
                    label => ReadTelemetry($"processor-{target}w-{passLabel}-{label}", plan), null, Lifetime);
                telemetry.AddRange(samples);
                List<LabIntelLimits> readbacks = [];
                for (var i = 0; i < 4; i++)
                {
                    readbacks.Add(await Task.Run(kx.Read));
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
                var outcome = await Task.Run(() => LabPowerRecovery.RestorePower(_machine, worker, new LabPowerLog()));
                page.Children.Add(RestoreLine(true, outcome.Restored, "processor power limit"));
                if (!outcome.Restored)
                {
                    page.Children.Add(Warning(outcome.Message + " Restart the device to reset the processor power limit."));
                }
                else
                {
                    await ReleaseQuietlyAsync(worker, kx, token);
                }

                tests.Add((result ?? Failed("processor-power", "kx", "stopped")) with { Restored = outcome.Restored });
                await Task.Run(() =>
                {
                    try
                    {
                        project.WriteEvidence(attempt, $"kx-log-{passLabel}", kx.CommandLog());
                    }
                    catch (InvalidOperationException ex)
                    {
                        // A lost worker took the command log with it; the stage log has the rest.
                        project.WriteEvidence(attempt, $"kx-log-{passLabel}", new { Unavailable = ex.Message });
                    }
                });
            }
        }
    }

    // Ends a checkpoint after a verified restore. A lost worker has no checkpoint left to end.
    private static Task ReleaseQuietlyAsync(LabWorkerClient worker, object service, string token)
    {
        return Task.Run(() =>
        {
            try
            {
                worker.Release(service, token);
            }
            catch (InvalidOperationException)
            {
                // The worker is gone; the restore it verified still stands.
            }
        });
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
}
