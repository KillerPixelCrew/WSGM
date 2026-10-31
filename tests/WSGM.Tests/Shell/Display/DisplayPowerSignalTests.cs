using WSGM.Interop;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class DisplayPowerSignalTests
{
    [Theory]
    [InlineData(DisplayPowerSignal.DisplayOff, true)]
    [InlineData(DisplayPowerSignal.DisplayOn, false)]
    [InlineData(DisplayPowerSignal.DisplayDimmed, false)]
    [InlineData(3, false)]
    [InlineData(99, false)]
    [InlineData(-1, false)]
    public void OnlyTheDocumentedOffStateMeansDarkness(int state, bool off)
    {
        Assert.Equal(off, DisplayPowerSignal.IsDisplayOff(state));
    }

    [Theory]
    [InlineData(DisplayStateSource.Session, true)]
    [InlineData(DisplayStateSource.Console, false)]
    [InlineData(DisplayStateSource.LegacyMonitor, false)]
    [InlineData((DisplayStateSource)99, false)]
    public void OnlyThisSessionsDisplayMayReportDarkness(DisplayStateSource source, bool authoritative)
    {
        Assert.Equal(authoritative, DisplayPowerSignal.MayReportDark(source));
    }
}
