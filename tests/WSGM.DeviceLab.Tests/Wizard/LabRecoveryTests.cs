using System.Text.Json;
using WSGM.DeviceLab.Preflight;
using WSGM.DeviceLab.Transports;
using WSGM.DeviceLab.Wizard;
using WSGM.DeviceLab.Worker;
using WSGM.Testing;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabRecoveryTests
{
    [Fact]
    public void Recovery_WhenReservationIsRefused_KeepsControllerRecordsAndStartsNoWorker()
    {
        using TemporaryDirectory temporary = new();
        var machine = new LabMachineState(temporary.GetPath("machine.json"));
        RecordController(machine);
        File.WriteAllText(machine.SidePath("mode-commands.json"), "[]");

        var notices = LabRecovery.Run(machine, false, new LabPawnIo(machine, new PawnIoHost()),
            () => throw new InvalidOperationException("No worker may start."),
            () => new DeviceLabOwnerReservationResult
            {
                Inspection = new DeviceLabOwnerInspection { State = DeviceOwnerDiscoveryState.Present }
            }, CancellationToken.None);

        Assert.Contains(notices, notice => notice.Contains("Close WSGM", StringComparison.Ordinal));
        Assert.NotNull(LabControllerInit.ReadPending(machine));
        Assert.True(File.Exists(machine.SidePath("mode-commands.json")));
    }

    [Fact]
    public void Recovery_WhenEarlierItemsFail_StillRestoresIntelRumbleAndControllerUnderOneReservation()
    {
        using TemporaryDirectory temporary = new();
        var machine = new LabMachineState(temporary.GetPath("machine.json"));
        var original = new LabIntelLimits(120, 160, 0, 0.125);
        machine.Update(changes => changes with
        {
            Power = new LabPowerChanges
            {
                RecordId = LabPowerChanges.ProcessorRecordId,
                RecordedAt = DateTimeOffset.UtcNow,
                AmdLimits = new LabAmdLimits(10, 15, 12),
                IntelLimits = original
            },
            Rumble = [new LabPendingRumbleRoute(null, "gone", "pad")],
            CuratedInitRecordId = "wsgm.claw-8-a2vm"
        });
        RecordController(machine);
        ReservationHandle handle = new();
        FakeWorker worker = new(original, handle);
        var reservations = 0;

        var notices = LabRecovery.Run(machine, true, new LabPawnIo(machine, new PawnIoHost()),
            () => worker, () =>
            {
                reservations++;
                handle.Held = true;
                return new DeviceLabOwnerReservationResult
                {
                    Inspection = new DeviceLabOwnerInspection { State = DeviceOwnerDiscoveryState.Absent },
                    Reservation = new DeviceLabOwnerReservation(handle)
                };
            }, CancellationToken.None);

        Assert.Contains(notices, notice => notice.Contains("PawnIO test failure", StringComparison.Ordinal));
        Assert.Contains(notices, notice => notice.Contains("AMD test timeout", StringComparison.Ordinal));
        Assert.Equal(1, reservations);
        Assert.False(handle.Held);
        Assert.Equal(1, worker.Intel.Restores);
        Assert.Equal(1, worker.Controller.Restores);
        Assert.NotNull(machine.Read().Power!.AmdLimits);
        Assert.Null(machine.Read().Power!.IntelLimits);
        Assert.Empty(machine.Read().Rumble);
        Assert.Null(LabControllerInit.ReadPending(machine));
        Assert.Null(machine.Read().CuratedInitRecordId);
        Assert.Equal(2, worker.Releases);
    }

    [Fact]
    public void Recovery_WhenModeCommandsAreUnreadable_StillRestoresControllerAndKeepsTheUnreadableFile()
    {
        using TemporaryDirectory temporary = new();
        var machine = new LabMachineState(temporary.GetPath("machine.json"));
        RecordController(machine);
        var modes = machine.SidePath("mode-commands.json");
        File.WriteAllText(modes, "{broken");
        ReservationHandle handle = new() { Held = true };
        FakeWorker worker = new(new LabIntelLimits(120, 160, 0, 0.125), handle);

        var notices = LabRecovery.Run(machine, false, new LabPawnIo(machine, new PawnIoHost()),
            () => worker, () => new DeviceLabOwnerReservationResult
            {
                Inspection = new DeviceLabOwnerInspection { State = DeviceOwnerDiscoveryState.Absent },
                Reservation = new DeviceLabOwnerReservation(handle)
            }, CancellationToken.None);

        Assert.Contains(notices, notice => notice.Contains("mode-command", StringComparison.Ordinal));
        Assert.Equal(1, worker.Controller.Restores);
        Assert.Null(LabControllerInit.ReadPending(machine));
        Assert.Equal("{broken", File.ReadAllText(modes));
        Assert.False(handle.Held);
    }

    private static void RecordController(LabMachineState machine)
    {
        var pending = new LabPendingControllerMode("0DB0", 1, new Dictionary<string, string>());
        File.WriteAllText(machine.SidePath("controller-mode.json"), JsonSerializer.Serialize(pending,
            LabProject.JsonOptions));
    }

    private sealed class ReservationHandle : IDisposable
    {
        public bool Held { get; set; }

        public void Dispose()
        {
            Held = false;
        }
    }

    private sealed class PawnIoHost : IPawnIoHost
    {
        public PawnIoStatus Detect()
        {
            throw new InvalidOperationException("PawnIO test failure");
        }

        public Task<string?> InstallAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<string?> UninstallAsync(PawnIoStatus status, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeWorker(LabIntelLimits original, ReservationHandle reservation) : ILabWorkerClient
    {
        public IntelService Intel { get; } = new(original);
        public ControllerService Controller { get; } = new();
        public int Releases { get; private set; }

        public T Open<T>(string service, LabPowerLog? log, params object?[] args) where T : class, IDisposable
        {
            Assert.True(reservation.Held);
            if (typeof(T) == typeof(ILabAmdSmu))
            {
                throw new TimeoutException("AMD test timeout");
            }

            if (typeof(T) == typeof(ILabRumbleWorker))
            {
                throw new LabRumbleRouteGoneException("The test route is gone.");
            }

            return typeof(T) == typeof(ILabIntelKx) ? (T)(object)Intel : (T)(object)Controller;
        }

        public (TState Original, string Token) Checkpoint<TState>(object service, Action<TState> persist)
        {
            var snapshot = service is IntelService intel ? (TState)(object)intel.Read() : default!;
            persist(snapshot);
            return (snapshot, "checkpoint");
        }

        public void Release(object service, string token)
        {
            Assert.True(reservation.Held);
            Assert.Equal("checkpoint", token);
            Releases++;
        }

        public string? StreamError(object service)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class IntelService(LabIntelLimits original) : ILabIntelKx
    {
        public int Restores { get; private set; }

        public LabIntelLimits Read()
        {
            return original;
        }

        public string? Restore(LabIntelLimits state)
        {
            Assert.Equal(original, state);
            Restores++;
            return null;
        }

        public string? WritePl1(LabIntelLimits state, double watts)
        {
            throw new NotSupportedException();
        }

        public IReadOnlyList<string> CommandLog()
        {
            return [];
        }

        public void Dispose()
        {
        }
    }

    private sealed class ControllerService : ILabCuratedInitWorker
    {
        public int Restores { get; private set; }

        public string? RecoverControllerMode(LabPendingControllerMode pending, CancellationToken cancellationToken)
        {
            Assert.Equal(1, pending.OriginalMode);
            Restores++;
            return null;
        }

        public int? Original()
        {
            return null;
        }

        public LabInitResult Send(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public int? CurrentMode()
        {
            throw new NotSupportedException();
        }

        public void Dispose()
        {
        }
    }
}
