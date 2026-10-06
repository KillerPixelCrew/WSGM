using WSGM.DeviceLab.Capture.Live;
using WSGM.DeviceLab.Wizard;
using WSGM.Testing;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabWmiQuarantineTests
{
    private const string Event = @"root\wmi\FirmwareButtonEvent";

    [Fact]
    public void RecoverFromCrash_KeepsTheClassBlockedBesideTheMachineRecord()
    {
        using TemporaryDirectory temporary = new();
        var machine = new LabMachineState(temporary.GetPath("wizard", "machine.json"));

        Assert.True(LabWmiQuarantine.Begin(Event, machine));
        Assert.Equal(Event, File.ReadAllText(machine.SidePath("wmi-enabling.txt")));
        Assert.Equal(Event, LabWmiQuarantine.RecoverFromCrash(machine));
        Assert.True(LabWmiQuarantine.IsBlocked(Event, machine));
        Assert.False(File.Exists(machine.SidePath("wmi-enabling.txt")));
        Assert.Null(LabWmiQuarantine.RecoverFromCrash(machine));
        Assert.False(File.Exists(machine.Path));
    }

    [Fact]
    public void RecoverFromCrash_WhenTheBlockedListCannotBeRead_KeepsThePendingMarker()
    {
        using TemporaryDirectory temporary = new();
        var machine = new LabMachineState(temporary.GetPath("machine.json"));
        Assert.True(LabWmiQuarantine.Begin(Event, machine));
        var blocked = machine.SidePath("wmi-blocked.txt");
        File.WriteAllText(blocked, Event);
        using (new FileStream(blocked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => LabWmiQuarantine.IsBlocked(Event, machine));
            Assert.Throws<IOException>(() => LabWmiQuarantine.RecoverFromCrash(machine));
            Assert.Equal(Event, File.ReadAllText(machine.SidePath("wmi-enabling.txt")));
        }
    }

    [Fact]
    public void IsBlocked_WhenTheListPathIsADirectory_DoesNotTreatItAsAbsent()
    {
        using TemporaryDirectory temporary = new();
        var machine = new LabMachineState(temporary.GetPath("machine.json"));
        Directory.CreateDirectory(machine.SidePath("wmi-blocked.txt"));

        Assert.Throws<UnauthorizedAccessException>(() => LabWmiQuarantine.IsBlocked(Event, machine));
    }

    [Fact]
    public void Begin_WhenTheMarkerCannotBeWritten_RefusesAndPreservesItsBytes()
    {
        using TemporaryDirectory temporary = new();
        var machine = new LabMachineState(temporary.GetPath("machine.json"));
        var pending = machine.SidePath("wmi-enabling.txt");
        File.WriteAllText(pending, Event);
        using (new FileStream(pending, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(LabWmiQuarantine.Begin("OtherEvent", machine));
        }

        Assert.Equal(Event, File.ReadAllText(pending));
    }

    [Fact]
    public void End_AfterADisableReturns_RemovesOnlyThePendingMarker()
    {
        using TemporaryDirectory temporary = new();
        var machine = new LabMachineState(temporary.GetPath("machine.json"));
        File.WriteAllText(machine.SidePath("wmi-blocked.txt"), "EarlierEvent");
        Assert.True(LabWmiQuarantine.Begin(Event, machine));

        LabWmiQuarantine.End(machine);

        Assert.False(File.Exists(machine.SidePath("wmi-enabling.txt")));
        Assert.True(LabWmiQuarantine.IsBlocked("EarlierEvent", machine));
    }

    [Fact]
    public void FirmwareTable_AboveTheAuthorizedByteBound_IsRefused()
    {
        var table = new byte[8 * 1024 * 1024 + 1];

        var failure = Assert.Throws<InvalidDataException>(() => LabWmiFirmwareEvents.ParseWdgEvents(table));

        Assert.Contains(table.Length.ToString(), failure.Message);
    }
}
