using Avalonia;
using Avalonia.Layout;
using Avalonia.Media;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests;

public class SplashStyleTests
{
    private static readonly Size Screen = new(1920, 1080);
    private static readonly Size Element = new(200, 100);

    private static SplashElementLayout Map(SplashElementPlacement placement) =>
        SplashStyle.MapPlacement(placement, Screen, Element);

    // ---- Anchor mode: all nine anchors ----

    [Theory]
    [InlineData(SplashPlacementAnchor.TopLeft, HorizontalAlignment.Left, VerticalAlignment.Top)]
    [InlineData(SplashPlacementAnchor.TopCenter, HorizontalAlignment.Center, VerticalAlignment.Top)]
    [InlineData(SplashPlacementAnchor.TopRight, HorizontalAlignment.Right, VerticalAlignment.Top)]
    [InlineData(SplashPlacementAnchor.CenterLeft, HorizontalAlignment.Left, VerticalAlignment.Center)]
    [InlineData(SplashPlacementAnchor.Center, HorizontalAlignment.Center, VerticalAlignment.Center)]
    [InlineData(SplashPlacementAnchor.CenterRight, HorizontalAlignment.Right, VerticalAlignment.Center)]
    [InlineData(SplashPlacementAnchor.BottomLeft, HorizontalAlignment.Left, VerticalAlignment.Bottom)]
    [InlineData(SplashPlacementAnchor.BottomCenter, HorizontalAlignment.Center, VerticalAlignment.Bottom)]
    [InlineData(SplashPlacementAnchor.BottomRight, HorizontalAlignment.Right, VerticalAlignment.Bottom)]
    public void EveryAnchorMapsToItsAlignmentPair(
        SplashPlacementAnchor anchor, HorizontalAlignment expectedH, VerticalAlignment expectedV)
    {
        var layout = Map(new SplashElementPlacement { Mode = SplashPlacementMode.Anchor, Anchor = anchor });

        Assert.False(layout.IsAbsolute);
        Assert.Equal(expectedH, layout.HorizontalAlignment);
        Assert.Equal(expectedV, layout.VerticalAlignment);
    }

    // ---- Anchor mode: padding on anchored edges, ignored on centered axes ----

    [Fact]
    public void TopLeftAnchorPutsPaddingOnLeftAndTopEdgesOnly()
    {
        var layout = Map(new SplashElementPlacement
        {
            Anchor = SplashPlacementAnchor.TopLeft,
            PaddingX = 40,
            PaddingY = 24,
        });

        Assert.Equal(new Thickness(40, 24, 0, 0), layout.Margin);
    }

    [Fact]
    public void TopRightAnchorPutsPaddingOnRightAndTopEdgesOnly()
    {
        var layout = Map(new SplashElementPlacement
        {
            Anchor = SplashPlacementAnchor.TopRight,
            PaddingX = 40,
            PaddingY = 24,
        });

        Assert.Equal(new Thickness(0, 24, 40, 0), layout.Margin);
    }

    [Fact]
    public void CenterAnchorIgnoresPaddingOnBothAxes()
    {
        var layout = Map(new SplashElementPlacement
        {
            Anchor = SplashPlacementAnchor.Center,
            PaddingX = 99,
            PaddingY = 77,
        });

        Assert.Equal(new Thickness(0), layout.Margin);
    }

    [Fact]
    public void TopCenterAnchorIgnoresHorizontalPaddingButKeepsVertical()
    {
        var layout = Map(new SplashElementPlacement
        {
            Anchor = SplashPlacementAnchor.TopCenter,
            PaddingX = 99,
            PaddingY = 30,
        });

        Assert.Equal(new Thickness(0, 30, 0, 0), layout.Margin);
    }

    [Fact]
    public void CenterLeftAnchorIgnoresVerticalPaddingButKeepsHorizontal()
    {
        var layout = Map(new SplashElementPlacement
        {
            Anchor = SplashPlacementAnchor.CenterLeft,
            PaddingX = 55,
            PaddingY = 99,
        });

        Assert.Equal(new Thickness(55, 0, 0, 0), layout.Margin);
    }

    [Fact]
    public void NegativePaddingIsTreatedAsZero()
    {
        var layout = Map(new SplashElementPlacement
        {
            Anchor = SplashPlacementAnchor.TopLeft,
            PaddingX = -50,
            PaddingY = -50,
        });

        Assert.Equal(new Thickness(0), layout.Margin);
    }

    // ---- Anchor mode: bottom-row clearance for the desktop button ----

    [Theory]
    [InlineData(SplashPlacementAnchor.BottomLeft)]
    [InlineData(SplashPlacementAnchor.BottomCenter)]
    [InlineData(SplashPlacementAnchor.BottomRight)]
    public void BottomRowAnchorsGetAtLeast128BottomMarginToClearTheDesktopButton(
        SplashPlacementAnchor anchor)
    {
        var layout = Map(new SplashElementPlacement { Anchor = anchor, PaddingY = 10 });

        Assert.Equal(128, layout.Margin.Bottom);
    }

    [Fact]
    public void BottomPaddingLargerThanTheClearanceWins()
    {
        var layout = Map(new SplashElementPlacement
        {
            Anchor = SplashPlacementAnchor.BottomCenter,
            PaddingY = 200,
        });

        Assert.Equal(200, layout.Margin.Bottom);
    }

    [Fact]
    public void TopRowAnchorsDoNotGetTheBottomClearance()
    {
        var layout = Map(new SplashElementPlacement
        {
            Anchor = SplashPlacementAnchor.TopCenter,
            PaddingY = 10,
        });

        Assert.Equal(0, layout.Margin.Bottom);
        Assert.Equal(10, layout.Margin.Top);
    }

    // ---- Absolute mode: clamping into screen bounds ----

    [Fact]
    public void AbsolutePlacementInsideTheScreenPassesThroughUnchanged()
    {
        var layout = Map(new SplashElementPlacement
        {
            Mode = SplashPlacementMode.Absolute,
            X = 300,
            Y = 400,
        });

        Assert.True(layout.IsAbsolute);
        Assert.Equal(300, layout.CanvasX);
        Assert.Equal(400, layout.CanvasY);
    }

    [Fact]
    public void AbsolutePlacementBeyondTheRightAndBottomEdgesIsClampedToKeepTheElementVisible()
    {
        var layout = Map(new SplashElementPlacement
        {
            Mode = SplashPlacementMode.Absolute,
            X = 5000,
            Y = 5000,
        });

        // Screen 1920x1080, element hint 200x100 → max top-left is 1720/980.
        Assert.Equal(1720, layout.CanvasX);
        Assert.Equal(980, layout.CanvasY);
    }

    [Fact]
    public void NegativeAbsoluteCoordinatesClampToZero()
    {
        var layout = Map(new SplashElementPlacement
        {
            Mode = SplashPlacementMode.Absolute,
            X = -500,
            Y = -1,
        });

        Assert.Equal(0, layout.CanvasX);
        Assert.Equal(0, layout.CanvasY);
    }

    [Fact]
    public void ElementLargerThanTheScreenPinsToTheTopLeftInsteadOfGoingNegative()
    {
        var layout = SplashStyle.MapPlacement(
            new SplashElementPlacement { Mode = SplashPlacementMode.Absolute, X = 100, Y = 100 },
            new Size(800, 600),
            new Size(1000, 700));

        Assert.Equal(0, layout.CanvasX);
        Assert.Equal(0, layout.CanvasY);
    }

    [Fact]
    public void AbsoluteModeIgnoresAnchorAndPadding()
    {
        var layout = Map(new SplashElementPlacement
        {
            Mode = SplashPlacementMode.Absolute,
            Anchor = SplashPlacementAnchor.BottomRight,
            PaddingX = 64,
            PaddingY = 64,
            X = 10,
            Y = 20,
        });

        Assert.True(layout.IsAbsolute);
        Assert.Equal(10, layout.CanvasX);
        Assert.Equal(20, layout.CanvasY);
        Assert.Equal(new Thickness(0), layout.Margin);
    }

    // ---- ParseColor ----

    [Fact]
    public void ParseColorAcceptsRgbHexStrings()
    {
        var color = SplashStyle.ParseColor("#FF9D3D", Colors.Black);

        Assert.Equal(Color.FromRgb(0xFF, 0x9D, 0x3D), color);
    }

    [Fact]
    public void ParseColorAcceptsArgbHexStringsAndSurroundingWhitespace()
    {
        var color = SplashStyle.ParseColor("  #80FF0000  ", Colors.Black);

        Assert.Equal(Color.FromArgb(0x80, 0xFF, 0x00, 0x00), color);
    }

    [Fact]
    public void ParseColorFallsBackOnGarbageInput()
    {
        var color = SplashStyle.ParseColor("not-a-color", Colors.Magenta);

        Assert.Equal(Colors.Magenta, color);
    }

    [Fact]
    public void ParseColorFallsBackOnEmptyInput()
    {
        Assert.Equal(Colors.White, SplashStyle.ParseColor("", Colors.White));
        Assert.Equal(Colors.White, SplashStyle.ParseColor("   ", Colors.White));
    }

    [Fact]
    public void ParseColorFallsBackOnNullInput()
    {
        Assert.Equal(Colors.Cyan, SplashStyle.ParseColor(null, Colors.Cyan));
    }
}
