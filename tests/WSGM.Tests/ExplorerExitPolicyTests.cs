using WSGM.Core;

namespace WSGM.Tests;

public sealed class ExplorerExitPolicyTests
{
    [Theory]
    [InlineData(true, false, 30000, 0)]
    [InlineData(true, true, 30000, 0)]
    [InlineData(false, false, 1999, 0)]
    [InlineData(false, false, 2000, 1)]
    [InlineData(false, true, 499, 0)]
    [InlineData(false, true, 500, 2)]
    public void OnlyARetiredOriginalShellCanBeReleased(bool present, bool exited, int absentMs, int expected) =>
        Assert.Equal((ExplorerExitAction)expected, ExplorerExitPolicy.Decide(present, exited, TimeSpan.FromMilliseconds(absentMs)));

    [Fact]
    public void AnUnresponsiveDesktopIsNotReadyEvenWithMatchingWindowOwners() =>
        Assert.False(ExplorerShellPolicy.IsInitializedShellOwner(true, true, 123, 123, responsive: false));
}
