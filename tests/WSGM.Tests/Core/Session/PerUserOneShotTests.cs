using WSGM.Install;

namespace WSGM.Tests.Core;

public sealed class PerUserOneShotTests
{
    [Theory]
    [InlineData("--set-uac-silent")]
    [InlineData("--restore-uac")]
    [InlineData("--disable-steam-autostart")]
    [InlineData("--restore-steam-autostart")]
    [InlineData("--disable-lock-on-wake")]
    [InlineData("--restore-lock-on-wake")]
    public void AnotherAccountsCredentialsPreventTheActualCommandDispatch(string flag)
    {
        var windowsWritten = false;
        var configurationWritten = false;
        List<string> refusals = [];

        var result = Program.RunOneShot([flag], () =>
        {
            if (!SetupUserIdentity.Matches(@"device\player", @"device\administrator"))
            {
                throw new WrongSetupAccountException();
            }
        }, (_, _) =>
        {
            windowsWritten = true;
            configurationWritten = true;
            return 0;
        }, refusals.Add);

        Assert.Equal(1, result);
        Assert.False(windowsWritten);
        Assert.False(configurationWritten);
        Assert.Single(refusals);
    }

    [Fact]
    public void TheMatchingAccountDispatchesExactlyTheSelectedCommand()
    {
        List<string> commands = [];
        var checkedAccount = false;

        var result = Program.RunOneShot(["--restore-uac"], () =>
        {
            Assert.True(SetupUserIdentity.Matches(@"device\player", @"DEVICE\PLAYER"));
            checkedAccount = true;
        }, (flag, _) =>
        {
            Assert.True(checkedAccount);
            commands.Add(flag);
            return 17;
        }, static _ => throw new InvalidOperationException("The matching account must not be refused."));

        Assert.Equal(17, result);
        Assert.Equal("--restore-uac", Assert.Single(commands));
    }
}
