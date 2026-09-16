using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WSGM.AllyXLab;

internal static class Worker
{
    internal static Action<object>? Checkpoint;
    internal static Action<object>? Progress;
    internal static Func<object, CancellationToken, string>? Interaction;
    [StructLayout(LayoutKind.Sequential)]
    internal struct PowerStatus
    { internal byte Ac, Flags, Percent, Saver; internal uint RemainingSeconds, FullSeconds; }
    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetSystemPowerStatus(out PowerStatus status);
    private static object PowerSource()
    {
        if (!GetSystemPowerStatus(out var p))
        {
            return new { Available = false };
        }

        return new { Available = true, AC = p.Ac, BatteryPercent = p.Percent, BatteryFlags = p.Flags };
    }
    internal static Result Run(Request request, CancellationToken cancel)
    {
        SessionLog log = new();
        string outcome = "not-started", cleanup = "not-needed";
        bool ownerAcquired = false;
        using Mutex owner = new(false, @"Global\WSGM.DeviceOwner");
        try
        {
            Limits.Validate(request);
            try { ownerAcquired = owner.WaitOne(0); }
            catch (AbandonedMutexException) { ownerAcquired = true; throw new InvalidOperationException("Previous device owner terminated unexpectedly. Reboot and restore OEM settings before testing."); }
            if (!ownerAcquired)
            {
                throw new InvalidOperationException("Another WSGM/device test owns this machine. Close it before collecting evidence.");
            }

            Identity identity = Identity.Read();
            List<HidEndpoint> endpoints = Hid.Enumerate();
            log.Add("identity", identity);
            log.Add("endpoints", endpoints.Select(e => e.Public).ToArray());
            log.Add("power-source", PowerSource());
            if (request.ExpectedAc is { } expected && (!GetSystemPowerStatus(out var expectedSource) || expectedSource.Ac != expected))
            {
                throw new InvalidOperationException("Power source does not match this wizard step. No write was attempted.");
            }

            string[] conflicts = Identity.ConflictingApps();
            log.Add("other-managers", conflicts);
            log.Add("provenance", new { Version = LabVersion.Text, Hhd = "5b49c5d904257e042a704ade958fac0ba57af4b1", Hc = "1d85da30861f700868e48ae8f498a5c455896f7c", Evidence = "Experimental attended Ally X bring-up, not production support" });
            if (request.Action == ActionKind.Inventory)
            {
                using var sensors = new Sensors(log);
                sensors.Poll();
                log.Add("motor-routes", Motors.Discover(endpoints));
                return new(request, "observed", cleanup, log.Events, null);
            }
            if (!identity.MatchesModel || !endpoints.Any(e => e.Vendor))
            {
                throw new InvalidOperationException("Identity gate requires ASUS ROG Ally X RC72LA (RC72L/RC72LA board, nonempty SKU) or ROG Xbox Ally X RC73XA (RC73XA board), a nonempty BIOS and ASUS 0B05:1B4C vendor usage FF31:0080. Export inventory if this legitimate device is refused; there is no override.");
            }

            if (Limits.Mutates(request.Action) && conflicts.Length > 0)
            {
                throw new InvalidOperationException("Close other device managers first: " + string.Join(", ", conflicts) + ". The wizard's device check can ask them to close; services are never stopped.");
            }

            void Revalidate()
            {
                cancel.ThrowIfCancellationRequested();
                if (Identity.Read().Fingerprint != identity.Fingerprint)
                {
                    throw new InvalidOperationException("Machine/firmware identity changed.");
                }

                var now = Hid.Enumerate();
                if (!now.Any(e => e.Vendor && endpoints.Any(old => old.Id == e.Id && old.Release == e.Release)))
                {
                    throw new InvalidOperationException("Device endpoint changed. Restart inventory.");
                }
            }
            if (request.Action == ActionKind.Capture)
            {
                using var input = new InputCapture(log, endpoints);
                using var sensors = request.Motion ? new Sensors(log) : null;
                Progress?.Invoke(new { Message = "CAPTURING NOW: " + request.Label, Seconds = request.Seconds });
                Stopwatch duration = Stopwatch.StartNew();
                while (duration.Elapsed.TotalSeconds < request.Seconds)
                {
                    cancel.ThrowIfCancellationRequested();
                    Application.DoEvents(); input.PollXInput(); sensors?.Poll(); Thread.Sleep(10);
                }
                input.Summary(); sensors?.Summarize();
                outcome = "captured";
            }
            else if (request.Action is ActionKind.ReadPower or ActionKind.Tdp or ActionKind.Profile or ActionKind.Fan)
            {
                using var control = new AsusControl(log);
                if (request.Action == ActionKind.ReadPower)
                {
                    // Collect each getter independently: an unsupported fan getter must not hide power evidence.
                    foreach (uint id in new[] { AsusControl.Mode, AsusControl.Spl, AsusControl.Sppt, AsusControl.Fppt, AsusControl.CpuSpeed, AsusControl.GpuSpeed })
                    {
                        try { log.Add("scalar", new { Id = $"{id:X8}", Value = control.Get(id) }); }
                        catch (Exception e) { log.Add("getter-unavailable", new { Id = $"{id:X8}", e.Message }); }
                    }
                    foreach (int mode in new[] { 0, 1, 2 })
                    {
                        foreach (uint id in new[] { AsusControl.CpuCurve, AsusControl.GpuCurve })
                        {
                            try { log.Add("fan-curve", new { Id = $"{id:X8}", Mode = mode, Curve = control.GetCurve(id, mode) }); }
                            catch (Exception e) { log.Add("getter-unavailable", new { Id = $"{id:X8}", Mode = mode, e.Message }); }
                        }
                    }
                    outcome = "observed";
                }
                else
                {
                    Revalidate();
                    if (!GetSystemPowerStatus(out var source) || source.Ac > 1 || source.Percent < 30 || source.Percent > 100)
                    {
                        throw new InvalidOperationException("Known AC/DC state and battery at least 30% are required.");
                    }

                    PowerState original = control.Snapshot();
                    Thread.Sleep(150);
                    if (!Same(original, control.Snapshot()))
                    {
                        throw new InvalidOperationException("Original state is changing. Close the competing manager before testing.");
                    }

                    log.Add("original", original);
                    Checkpoint?.Invoke(new { identity.Fingerprint, Request = request, Original = original, Source = PowerSource() });
                    // The parent durably saves and acknowledges the checkpoint before any write.
                    cleanup = "unverified";
                    bool matches = false;
                    try
                    {
                        cancel.ThrowIfCancellationRequested();
                        void Set(uint id, int value)
                        {
                            cancel.ThrowIfCancellationRequested();
                            if (!GetSystemPowerStatus(out var current) || current.Ac != source.Ac)
                            {
                                throw new InvalidOperationException("Power source changed; stopping the action.");
                            }

                            control.Set(id, value); Thread.Sleep(120);
                            int observed = control.Get(id);
                            if (observed != value)
                            {
                                throw new InvalidOperationException($"Immediate readback for {id:X8} was {observed}, expected {value}. Stopping without retry.");
                            }
                        }
                        byte[]? targetCurve = null;
                        if (request.Action == ActionKind.Profile)
                        {
                            Set(AsusControl.Mode, request.Value);
                        }
                        else if (request.Action == ActionKind.Tdp)
                        {
                            if (request.Value <= original.Sppt)
                            { Set(AsusControl.Spl, request.Value); Set(AsusControl.Sppt, request.Value); Set(AsusControl.Fppt, request.Value); }
                            else
                            { Set(AsusControl.Fppt, request.Value); Set(AsusControl.Sppt, request.Value); Set(AsusControl.Spl, request.Value); }
                        }
                        else
                        {
                            targetCurve = (byte[])(request.Channel == 0 ? original.CpuCurve : original.GpuCurve).Clone();
                            for (int i = 8; i < 16; i++)
                            {
                                targetCurve[i] = (byte)Math.Min(99, targetCurve[i] + request.Value);
                            }

                            if (targetCurve.Skip(8).Zip(request.Channel == 0 ? original.CpuCurve.Skip(8) : original.GpuCurve.Skip(8)).Any(x => x.First < x.Second))
                            {
                                throw new InvalidOperationException("The fan test would reduce a captured duty value.");
                            }

                            control.SetCurve(request.Channel == 0 ? AsusControl.CpuCurve : AsusControl.GpuCurve, targetCurve);
                        }
                        for (int i = 0; i < 4; i++)
                        {
                            Wait(1000, cancel);
                            if (!GetSystemPowerStatus(out var current) || current.Ac != source.Ac)
                            {
                                throw new InvalidOperationException("Power source changed during readback.");
                            }

                            PowerState readback = control.Snapshot();
                            matches = request.Action switch
                            {
                                ActionKind.Tdp => readback.Spl == request.Value && readback.Sppt == request.Value && readback.Fppt == request.Value,
                                ActionKind.Profile => readback.Mode == request.Value,
                                _ => (request.Channel == 0 ? readback.CpuCurve : readback.GpuCurve).SequenceEqual(targetCurve!),
                            };
                            log.Add("readback", new { Sample = i + 1, State = readback, MatchesRequested = matches, Source = PowerSource() });
                            foreach (uint id in new[] { AsusControl.CpuSpeed, AsusControl.GpuSpeed })
                            {
                                try { log.Add("fan-speed-raw", new { Id = id, Value = control.Get(id), Units = "driver units; not assumed RPM" }); }
                                catch (Exception e) { log.Add("fan-speed-unavailable", e.Message); }
                            }
                        }
                        outcome = matches ? "applied-readback-matched" : "readback-mismatch";
                    }
                    finally
                    {
                        // Restore only the state of the same live power source. Replaying an AC envelope
                        // after unplugging would be another unvalidated hardware action.
                        if (GetSystemPowerStatus(out var current) && current.Ac == source.Ac)
                        {
                            cleanup = control.Restore(original) ? "restored-readback-matched" : "RESTORATION FAILED";
                        }
                        else { log.Add("restore-refused", "Power source changed. Restore the recorded OEM profile manually."); cleanup = "RESTORATION FAILED: power source changed"; }
                    }
                }
            }
            else
            {
                Revalidate();
                if (request.Action is ActionKind.RumbleCalibration or ActionKind.RumbleProbe)
                {
                    // Whichever route the device actually answers: HHD's output report, HC's XInput
                    // vibration, or Windows.Gaming.Input. The route is chosen by the wizard from what
                    // the tester felt, never guessed here.
                    using IMotorOutput motor = Motors.Open(request.Endpoint, endpoints, log);
                    Checkpoint?.Invoke(new
                    {
                        identity.Fingerprint,
                        Request = request,
                        Route = motor.Route,
                        Original = "Silent baseline; motors are zeroed after every pulse and confirmed by the tester"
                    });
                    cleanup = "unverified";
                    try
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (request.Action == ActionKind.RumbleProbe)
                        {
                            RumbleCalibration.Probe(motor, log, cancel);
                            outcome = "write-returned-awaiting-operator-observation";
                        }
                        else
                        {
                            RumbleCalibration.Run(motor, log, cancel);
                            outcome = "calibration-complete";
                        }
                    }
                    finally
                    {
                        try { motor.Zero(); cleanup = "zero-output-sent; operator confirmation required"; }
                        catch (Exception e) { log.Add("zero-output-error", e.Message); cleanup = "RESTORATION FAILED"; }
                    }

                    return new(request, outcome, cleanup, log.Events, null);
                }

                HidEndpoint[] selected = [.. endpoints.Where(e => e.Vendor && e.Id == request.Endpoint)];
                HidEndpoint endpoint = selected.Length == 1
                    ? selected[0]
                    : throw new InvalidOperationException("Select exactly one inventoried endpoint for this action.");
                if (endpoint.OutputBytes < 64)
                {
                    throw new InvalidOperationException("Expected output report is absent. No feature-report fallback is attempted.");
                }

                using var handle = Hid.Open(endpoint);
                Checkpoint?.Invoke(new
                {
                    identity.Fingerprint,
                    Request = request,
                    Endpoint = endpoint.Public,
                    Original = "Operator asserted lights off; exact prior color/mode is not readable"
                });
                cleanup = "unverified";
                try
                {
                    cancel.ThrowIfCancellationRequested();
                    Hid.Output(handle, endpoint, [0x5A, .. System.Text.Encoding.ASCII.GetBytes("ASUS Tech.Inc.")], log);
                    Hid.Output(handle, endpoint, [0x5A, 0xBA, 0xC5, 0xC4, 1], log);
                    byte[] color = new byte[64]; color[0] = 0x5A; color[1] = 0xB3; color[2] = (byte)request.Channel;
                    color[4 + request.Value] = 80;
                    Hid.Output(handle, endpoint, color, log);
                    Hid.Output(handle, endpoint, [0x5A, 0xB5], log);
                    Hid.Output(handle, endpoint, [0x5A, 0xB4], log);
                    Wait(2000, cancel);
                    outcome = "write-returned-awaiting-operator-observation";
                }
                finally
                {
                    try
                    {
                        Hid.Output(handle, endpoint, [0x5A, 0xBA, 0xC5, 0xC4, 0], log);
                        cleanup = "zero-output-sent; operator confirmation required";
                    }
                    catch (Exception e) { log.Add("zero-output-error", e.Message); cleanup = "RESTORATION FAILED"; }
                }
            }
            return new(request, outcome, cleanup, log.Events, null);
        }
        catch (Exception e) { return new(request, "failed-or-cancelled", cleanup, log.Events, e.Message); }
        finally
        {
            if (ownerAcquired)
            {
                owner.ReleaseMutex();
            }
        }
    }
    private static bool Same(PowerState a, PowerState b) => a.Mode == b.Mode && a.Spl == b.Spl && a.Sppt == b.Sppt && a.Fppt == b.Fppt && a.CpuCurve.SequenceEqual(b.CpuCurve) && a.GpuCurve.SequenceEqual(b.GpuCurve);
    private static void Wait(int milliseconds, CancellationToken cancel)
    {
        if (cancel.WaitHandle.WaitOne(milliseconds))
        {
            cancel.ThrowIfCancellationRequested();
        }
    }
}
