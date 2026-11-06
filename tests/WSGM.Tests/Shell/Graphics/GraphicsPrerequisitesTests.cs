using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;
using static WSGM.Tests.Builders.CapabilityBuilders;

namespace WSGM.Tests.Shell;

public sealed class GraphicsPrerequisitesTests
{
    private const string Afmf = "graphics.afmf";
    private const string AntiLag = "graphics.anti-lag";

    private static readonly DeviceCapabilityView AfmfView =
        View(Toggle(CapabilityProfileScope.Switched, Afmf) with { Prerequisite = AntiLag }, null, null);

    private static readonly DeviceCapabilityView AntiLagView =
        View(Toggle(CapabilityProfileScope.Switched, AntiLag), null, null);

    [Fact]
    public void OnlyTurningOnADeclaredDependentNeedsItsPrerequisite()
    {
        Assert.Equal(AntiLag, GraphicsPrerequisites.Required(AfmfView, Flag(true)));
        Assert.Null(GraphicsPrerequisites.Required(AfmfView, Flag(false)));
        Assert.Null(GraphicsPrerequisites.Required(
            View(Toggle(CapabilityProfileScope.Switched, "graphics.afmf-algorithm"), null, null), Flag(true)));
        Assert.Null(GraphicsPrerequisites.Required(AntiLagView, Flag(true)));
    }

    [Fact]
    public void ADeclaredPrerequisiteSortsAheadOfEveryOtherGraphicsSetting()
    {
        var priority = GraphicsPrerequisites.Priority([AfmfView, AntiLagView]);

        Assert.True(priority(AntiLagView) < priority(AfmfView));
    }

    [Fact]
    public void NothingIsPrioritisedWithoutADeclaration()
    {
        var plainAfmf = View(Toggle(CapabilityProfileScope.Switched, Afmf), null, null);
        var priority = GraphicsPrerequisites.Priority([plainAfmf, AntiLagView]);

        Assert.Equal(priority(plainAfmf), priority(AntiLagView));
    }
}
