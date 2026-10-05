using WSGM.DeviceLab.Transports;
using WSGM.DeviceLab.Wizard;
using WSGM.Testing;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabPowerRecoveryTests
{

    [Fact]
    public void Record_RefusesADifferentDeviceWhileChangesAreStillPending()
    {
        using TemporaryDirectory temporary = new();
        var machine = new LabMachineState(Path.Combine(temporary.Root, "machine.json"));
        LabPowerRecovery.Record(machine, "wsgm.rog-ally-x",
            changes => changes with { MsiChargeRaw = 0xE0 });

        Assert.Throws<InvalidOperationException>(() => LabPowerRecovery.Record(machine, "wsgm.claw-8-a2vm",
            changes => changes with { MsiChargeRaw = 0xE0 }));
    }

    [Fact]
    public void Recovery_WithNothingRecorded_StartsNoWorkerAndReservesNothing()
    {
        using TemporaryDirectory temporary = new();
        var machine = new LabMachineState(Path.Combine(temporary.Root, "machine.json"));

        var notices = LabRecovery.Run(machine, elevated: false, LabPawnIo.ForMachine(machine),
            () => throw new InvalidOperationException("No worker may start."),
            () => throw new InvalidOperationException("Nothing may be reserved."), CancellationToken.None);

        Assert.Empty(notices);
    }

    [Fact]
    public void ClearAura_RemovesTheLightingNoteAndTheEmptyRecord()
    {
        using TemporaryDirectory temporary = new();
        var machine = new LabMachineState(Path.Combine(temporary.Root, "machine.json"));
        LabPowerRecovery.Record(machine, "wsgm.rog-ally-x",
            changes => changes with { AuraWrittenAt = DateTimeOffset.UtcNow });

        LabPowerRecovery.ClearAura(machine);

        Assert.Null(machine.Read().Power);
    }

    [Fact]
    public void Record_ThenClearField_LeavesNoPendingPowerChange()
    {
        using TemporaryDirectory temporary = new();
        var machine = new LabMachineState(Path.Combine(temporary.Root, "machine.json"));
        var original = new LabAsusState(0, 15, 20, 25, "AABB", "CCDD");
        LabPowerRecovery.Record(machine, "wsgm.rog-ally-x",
            changes => changes with { AcLine = 1, AsusPower = original });

        Assert.True(LabPowerChanges.PowerPending(machine.Read().Power));
        machine.Update(changes => changes with { Power = changes.Power! with { AsusPower = null } });

        Assert.False(LabPowerChanges.PowerPending(machine.Read().Power));
    }
}
