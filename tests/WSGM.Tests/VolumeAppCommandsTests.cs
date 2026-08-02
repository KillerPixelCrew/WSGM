using WSGM.Shell;

namespace WSGM.Tests;

public sealed class VolumeAppCommandsTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void FromShellHookLParam_DecodesPackedVolumeCommand(int command)
    {
        var packed = (nint)(command << 16);

        Assert.Equal(command, VolumeAppCommands.FromShellHookLParam(packed));
    }

    [Fact]
    public void FromShellHookLParam_AcceptsOemAlreadyExtractedCommand()
    {
        Assert.Equal(VolumeAppCommands.Up, VolumeAppCommands.FromShellHookLParam(VolumeAppCommands.Up));
    }

    [Fact]
    public void FromShellHookLParam_IgnoresNonVolumeCommand()
    {
        Assert.Equal(0, VolumeAppCommands.FromShellHookLParam((nint)(14 << 16)));
    }
}
