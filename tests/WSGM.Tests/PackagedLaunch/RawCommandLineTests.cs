extern alias packagedLaunch;
using packagedLaunch::WSGM.PackagedLaunch;

namespace WSGM.Tests.PackagedLaunch;

/// <summary>
///     The follow mode's own command line, cut after the launcher's path and otherwise untouched. The
///     line is Windows' raw one: an argument array joined back together has already lost a folder's
///     trailing backslash to the quote after it, and re-quoted a launcher's own arguments.
/// </summary>
public sealed class RawCommandLineTests
{
    private const string Tail =
        "--follow --dir \"C:\\Games\\Overwatch\\\" -- \"C:\\Battle.net\\Battle.net.exe\" --exec=\"launch Pro\"";

    [Fact]
    public void EverythingAfterAQuotedProgramIsReturnedVerbatim()
    {
        Assert.Equal(Tail, RawCommandLine.Arguments("\"C:\\Program Files\\WSGM\\WSGM.PackagedLaunch.exe\" " + Tail));
    }

    [Fact]
    public void AnUnquotedProgramEndsAtTheFirstSpaceOrTab()
    {
        Assert.Equal(Tail, RawCommandLine.Arguments("C:\\WSGM\\WSGM.PackagedLaunch.exe\t" + Tail));
    }

    [Fact]
    public void AProgramAloneHasNoArguments()
    {
        Assert.Equal(string.Empty, RawCommandLine.Arguments("\"C:\\WSGM\\WSGM.PackagedLaunch.exe\""));
        Assert.Equal(string.Empty, RawCommandLine.Arguments("WSGM.PackagedLaunch.exe"));
    }
}
