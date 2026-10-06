using WSGM.DeviceLab.Wizard;
using WSGM.Testing;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabMachineStateTests
{
    [Fact]
    public void Read_WhenMissing_ReturnsEmptyWithoutCreatingTheRecord()
    {
        using TemporaryDirectory temporary = new();
        var machine = new LabMachineState(temporary.GetPath("missing", "machine.json"));

        Assert.Empty(machine.Read().Rumble);
        Assert.False(File.Exists(machine.Path));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    public void Update_WhenTheRecordIsInvalid_PreservesItsBytes(string text)
    {
        using TemporaryDirectory temporary = new();
        var machine = new LabMachineState(temporary.GetPath("machine.json"));
        File.WriteAllText(machine.Path, text);

        var failure = Assert.Throws<IOException>(() => machine.Read());
        Assert.Contains(machine.Path, failure.Message);
        Assert.Throws<IOException>(() => machine.Update(changes => changes with { HidHideEntry = "new-entry" }));
        Assert.Equal(text, File.ReadAllText(machine.Path));
    }

    [Fact]
    public void Read_WhenTheRecordPathIsADirectory_DoesNotTreatItAsAbsent()
    {
        using TemporaryDirectory temporary = new();
        var machine = new LabMachineState(temporary.Root);

        Assert.Throws<UnauthorizedAccessException>(() => machine.Read());
        Assert.Throws<UnauthorizedAccessException>(() => machine.Update(changes => changes));
        Assert.True(Directory.Exists(temporary.Root));
    }

    [Fact]
    public void Update_WhenTheRecordIsLocked_DoesNotReplaceIt()
    {
        using TemporaryDirectory temporary = new();
        var machine = new LabMachineState(temporary.GetPath("machine.json"));
        machine.Update(changes => changes with { HidHideEntry = "original-entry" });
        var bytes = File.ReadAllBytes(machine.Path);
        using (new FileStream(machine.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => machine.Update(changes => changes with { HidHideEntry = null }));
        }

        Assert.Equal(bytes, File.ReadAllBytes(machine.Path));
        Assert.Equal("original-entry", machine.Read().HidHideEntry);
    }
}
