using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class SteamControllerCapsSurfaceTests
{
    private static SteamInputGlyphPresentation Presentation(params GlyphControlId[] absent)
    {
        return new SteamInputGlyphPresentation("test", 1, [], [], absent, []);
    }

    [Fact]
    public void NoProfileClearsNothing()
    {
        Assert.Equal(0UL, SteamControllerCapsSurface.Mask(null));
        Assert.Equal("0", SteamControllerCapsSurface.State(null).Mask);
    }

    [Fact]
    public void BothTrackpadsAbsentClearsTheTrackpadBit()
    {
        var mask = SteamControllerCapsSurface.Mask(Presentation(GlyphControlId.LeftTrackpad,
            GlyphControlId.RightTrackpad));

        Assert.Equal(SteamControllerCapsSurface.TrackpadBit, mask);
    }

    [Fact]
    public void OneTrackpadAbsentLeavesTheBitBecauseSteamCannotExpressOnePad()
    {
        Assert.Equal(0UL, SteamControllerCapsSurface.Mask(Presentation(GlyphControlId.RightTrackpad)));
    }

    [Fact]
    public void BothStickTouchesAbsentClearsTheCapacitiveStickBit()
    {
        var mask = SteamControllerCapsSurface.Mask(Presentation(
            GlyphControlId.LeftStickTouch, GlyphControlId.RightStickTouch, GlyphControlId.RearLeft2,
            GlyphControlId.RearRight2));

        // The back-button pair is not mapped to a bit on purpose.
        Assert.Equal(SteamControllerCapsSurface.CapacitiveStickBit, mask);
    }

    [Fact]
    public void TheStateNamesTheSteamDeckCompositeAndCarriesTheMaskAsDecimal()
    {
        var state = SteamControllerCapsSurface.State(Presentation(
            GlyphControlId.LeftTrackpad, GlyphControlId.RightTrackpad,
            GlyphControlId.LeftStickTouch, GlyphControlId.RightStickTouch));

        Assert.Equal(0x28DE, state.VendorId);
        Assert.Equal(0x1205, state.ProductId);
        Assert.Equal(((1UL << 12) | (1UL << 24)).ToString(), state.Mask);
    }
}
