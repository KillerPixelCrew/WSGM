using WSGM.Setup.UI;

namespace WSGM.Tests.Setup;

public sealed class SummaryPageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrimaryActionStartsOnlyAfterSuccessfulInstallation(bool successfulInstall)
    {
        SummaryPage page = new("", "", "", [], "", "", "", successfulInstall);
        List<string> calls = [];

        page.Complete(() => calls.Add("start"), () => calls.Add("close"));

        Assert.Equal(successfulInstall ? ["start", "close"] : ["close"], calls);
    }
}
