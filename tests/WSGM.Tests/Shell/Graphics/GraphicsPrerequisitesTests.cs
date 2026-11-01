using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;
using static WSGM.Tests.Builders.CapabilityBuilders;

namespace WSGM.Tests.Shell;

public sealed class GraphicsPrerequisitesTests
{
    [Fact]
    public void OnlyTurningAfmfOnNeedsAntiLag()
    {
        Assert.Equal(GraphicsPrerequisites.AntiLag, GraphicsPrerequisites.Required(GraphicsPrerequisites.Afmf,
            Flag(true)));
        Assert.Null(GraphicsPrerequisites.Required(GraphicsPrerequisites.Afmf, Flag(false)));
        Assert.Null(GraphicsPrerequisites.Required("graphics.afmf-algorithm", Flag(true)));
        Assert.Null(GraphicsPrerequisites.Required(GraphicsPrerequisites.AntiLag, Flag(true)));
    }

    [Fact]
    public void AntiLagSortsAheadOfEveryOtherGraphicsSetting()
    {
        var antiLag = View(Toggle(CapabilityProfileScope.Switched, GraphicsPrerequisites.AntiLag), null, null);
        var afmf = View(Toggle(CapabilityProfileScope.Switched, GraphicsPrerequisites.Afmf), null, null);

        Assert.True(GraphicsPrerequisites.Priority(antiLag) < GraphicsPrerequisites.Priority(afmf));
    }
}
