using WSGM.Device.Tests;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabPowerRecoveryTests
{
    [Fact]
    public void AnyPending_And_PowerPending_TrackWhatIsRecorded()
    {
        Assert.False(LabPowerChanges.AnyPending(null));

        var withCharge = new LabPowerChanges { RecordId = "r", RecordedAt = DateTimeOffset.UtcNow, MsiChargeRaw = 0xE0 };
        Assert.True(LabPowerChanges.AnyPending(withCharge));
        Assert.True(LabPowerChanges.PowerPending(withCharge));

        var auraOnly = new LabPowerChanges
        {
            RecordId = "r",
            RecordedAt = DateTimeOffset.UtcNow,
            AuraWrittenAt = DateTimeOffset.UtcNow
        };
        Assert.True(LabPowerChanges.AnyPending(auraOnly));
        Assert.False(LabPowerChanges.PowerPending(auraOnly)); // Write-only lighting is not a power change.
    }

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
    public void RestoreRecorded_WithNothingRecorded_ReturnsNull()
    {
        using TemporaryDirectory temporary = new();
        var machine = new LabMachineState(Path.Combine(temporary.Root, "machine.json"));

        Assert.Null(LabPowerRecovery.RestoreRecorded(machine));
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
