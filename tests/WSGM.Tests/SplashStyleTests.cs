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

    // ---- Logo decode cap (BootSplashWindow) ----

    [Theory]
    [InlineData(200, 1000)]   // Default logo bound: 200 DIP -> 4 MB decoded, not 320 MB.
    [InlineData(1, 5)]
    [InlineData(3999, 19995)] // Just under ImageHeader.MaxDimension.
    public void TheLogoDecodeCapCoversTheWholeSupportedDisplayScaleRange(int maxSizeDips, int expected)
    {
        // DisplayScale supports 100-500%, and the renderer draws the logo in physical
        // pixels (DIP * scaling), so a headroom below 5 left everything above 300%
        // upscaled from a too-small decode and visibly soft.
        Assert.Equal(expected, BootSplashWindow.LogoDecodeCap(maxSizeDips));
    }

    [Theory]
    [InlineData(4096)]           // Largest logo bound ConfigStore's clamp allows.
    [InlineData(int.MaxValue)]   // A preview config is built without that clamp.
    public void TheLogoDecodeCapStaysWithinTheImageHeaderLimitAndCannotOverflow(int maxSizeDips)
    {
        // Beyond the largest edge ImageHeader accepts, the cap could never bind
        // anyway — and the multiplication must not wrap into a negative width.
        Assert.Equal(ImageHeader.MaxDimension, BootSplashWindow.LogoDecodeCap(maxSizeDips));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-200)]
    public void TheLogoDecodeCapNeverGoesBelowASinglePixel(int maxSizeDips)
    {
        Assert.True(BootSplashWindow.LogoDecodeCap(maxSizeDips) >= 1);
    }

    [Theory]
    [InlineData(1000, 1000, 1000)]  // Square: the cap is the longer edge either way.
    [InlineData(4000, 1000, 1000)]  // Landscape: width hits the cap.
    [InlineData(500, 2000, 250)]    // Portrait: height hits the cap, width follows the ratio.
    public void TheLogoDecodeWidthPutsTheRenderedLongerEdgeOnTheCap(
        int sourceWidth, int sourceHeight, int expected)
    {
        // 200 DIP * 5 headroom = a 1000 px cap on the rendered longer edge.
        Assert.Equal(expected, BootSplashWindow.LogoDecodeWidth(200, sourceWidth, sourceHeight));
    }

    [Fact]
    public void TheLogoDecodeWidthStaysPositiveAtTheImageHeaderLimits()
    {
        // cap * sourceWidth is 20000 * 20000 here, which wraps a 32-bit multiply.
        var width = BootSplashWindow.LogoDecodeWidth(
            int.MaxValue, ImageHeader.MaxDimension, ImageHeader.MaxDimension);

        Assert.InRange(width, 1, ImageHeader.MaxDimension);
        Assert.True(
            DecodedLogoPixels(int.MaxValue, ImageHeader.MaxDimension, ImageHeader.MaxDimension)
                <= BootSplashWindow.LogoDecodePixelBudget(int.MaxValue) + width,
            $"the largest source ImageHeader admits must stay inside the budget, decoded {width} px wide");
        Assert.True(BootSplashWindow.LogoDecodeWidth(int.MaxValue, 1, ImageHeader.MaxDimension) >= 1);
    }

    // ---- Logo decode budget (BootSplashWindow) ----

    /// <summary>The pixels a logo decode actually produces: the caller only ever
    /// scales DOWN, so a source narrower than its bound is decoded whole. This is the
    /// branch the per-edge cap alone never bounded.</summary>
    private static long DecodedLogoPixels(int maxSizeDips, int sourceWidth, int sourceHeight)
    {
        var bound = BootSplashWindow.LogoDecodeWidth(maxSizeDips, sourceWidth, sourceHeight);
        if (sourceWidth <= bound)
        {
            return (long)sourceWidth * sourceHeight;
        }
        var height = (long)Math.Ceiling((double)bound * sourceHeight / sourceWidth);
        return bound * height;
    }

    [Theory]
    [InlineData(200, 1_000_000)]            // Default: the 1000 px cap squared, 4 MB.
    [InlineData(1, 25)]                     // Smallest bound: 5 px squared.
    [InlineData(404, 4_080_400)]            // Largest bound still under the screen cover.
    [InlineData(4096, 2560 * 1600)]         // Largest bound ConfigStore allows: the cover ceiling.
    [InlineData(int.MaxValue, 2560 * 1600)] // A preview config is built without that clamp.
    public void TheLogoDecodeBudgetFollowsTheConfiguredBoundUpToTheScreenCover(
        int maxSizeDips, long expected)
    {
        // The budget is the area of the LogoMaxSize * 5 square the logo is fitted into
        // — it follows from the configured bound and the DPI headroom, not from the
        // display — and stops at the pixels a full-screen cover can show, which is
        // where the derived value (419 MP at 4096 DIP) stops meaning anything.
        Assert.Equal(expected, BootSplashWindow.LogoDecodePixelBudget(maxSizeDips));
    }

    [Theory]
    [InlineData(2000, 9999, 8000)]                              // The measured worst case at 2000.
    [InlineData(4096, ImageHeader.MaxDimension, 4000)]          // Widest allowed, 80 MP.
    [InlineData(4096, 4000, ImageHeader.MaxDimension)]          // Tallest allowed, 80 MP.
    [InlineData(4096, 8944, 8944)]                              // Largest allowed square.
    [InlineData(4096, ImageHeader.MaxDimension, 1)]             // Most extreme landscape ratio.
    [InlineData(4096, 1, ImageHeader.MaxDimension)]             // Most extreme portrait ratio.
    [InlineData(2000, 8000, 9999)]                              // Portrait twin of the worst case.
    [InlineData(200, 5000, 5000)]                               // Default bound, oversized source.
    public void TheLogoDecodeStaysInsideItsBudgetForEveryAdmissibleSource(
        int maxSizeDips, int sourceWidth, int sourceHeight)
    {
        // Before the area budget, a source narrower than the (clamped) per-edge cap
        // fell through to a whole-image decode: 79,995,136 px (~305 MiB) at LogoMaxSize
        // 2000 and 80,000,000 px at 4096 — and LogoMaxSize is carried by an untrusted
        // .wsgmsplash theme, on the boot path, at every sign-in.
        var budget = BootSplashWindow.LogoDecodePixelBudget(maxSizeDips);
        var width = BootSplashWindow.LogoDecodeWidth(maxSizeDips, sourceWidth, sourceHeight);
        var pixels = DecodedLogoPixels(maxSizeDips, sourceWidth, sourceHeight);

        Assert.InRange(width, 1, ImageHeader.MaxDimension);
        // + width: the decoded height rounds up to whole pixels.
        Assert.True(pixels <= budget + width, $"{sourceWidth}x{sourceHeight} decodes to {pixels} px");
        Assert.True(pixels < ImageHeader.MaxPixels / 4, $"{pixels} px is still on the old order of magnitude");
    }

    [Theory]
    [InlineData(200, 300, 300)]     // Small square, well inside the default budget.
    [InlineData(200, 100, 800)]     // Small but tall: the edge bound is the tighter one.
    [InlineData(200, 1000, 1000)]   // Exactly the default cap.
    [InlineData(4096, 2023, 2023)]  // Largest square that still fits the ceiling exactly.
    public void TheLogoDecodeIsNeverUpscaled(int maxSizeDips, int sourceWidth, int sourceHeight)
    {
        // The bound is an upper limit only: when it does not fall below the source's
        // own width, TryLoadBitmap skips DecodeToWidth entirely — and a source already
        // inside the budget is by construction never scaled down.
        Assert.True(
            BootSplashWindow.LogoDecodeWidth(maxSizeDips, sourceWidth, sourceHeight) >= sourceWidth,
            "a source inside both bounds must not be scaled at all");
        Assert.Equal(
            (long)sourceWidth * sourceHeight, DecodedLogoPixels(maxSizeDips, sourceWidth, sourceHeight));
    }

    [Fact]
    public void TheLogoDecodeWidthFallsBackToTheCapOnUnusableDimensions()
    {
        // ImageHeader gates these out before the bound is consulted; the helper still
        // must not divide by zero or return a nonsense width.
        Assert.Equal(1000, BootSplashWindow.LogoDecodeWidth(200, 0, 1080));
        Assert.Equal(1000, BootSplashWindow.LogoDecodeWidth(200, 1920, 0));
        Assert.Equal(1000, BootSplashWindow.LogoDecodeWidth(200, -1920, -1080));
    }

    // ---- Background decode budget (BootSplashWindow) ----

    /// <summary>2560x1600 — the widest supported panel at its own aspect ratio.</summary>
    private const long BackgroundPixelBudget = 2560L * 1600;

    /// <summary>The pixels a background decode actually produces: the caller only ever
    /// scales DOWN, so a source narrower than its bound is decoded whole.</summary>
    private static long DecodedPixels(int sourceWidth, int sourceHeight)
    {
        var bound = BootSplashWindow.BackgroundDecodeWidth(sourceWidth, sourceHeight);
        if (sourceWidth <= bound)
        {
            return (long)sourceWidth * sourceHeight;
        }
        var height = (long)Math.Ceiling((double)bound * sourceHeight / sourceWidth);
        return bound * height;
    }

    [Theory]
    [InlineData(3840, 2160)]  // 4K landscape source.
    [InlineData(2560, 1600)]  // Largest supported panel, 1:1.
    [InlineData(1920, 1200)]  // Typical handheld panel.
    [InlineData(1280, 800)]   // WSGM's floor.
    public void TheBackgroundDecodeWidthKeepsTheOldWidthCapForLandscapeSources(
        int sourceWidth, int sourceHeight)
    {
        // Any aspect at or wider than 16:10 hits the 2560 width cap before the area
        // budget, so realistic backgrounds decode exactly as they did before.
        Assert.Equal(2560, BootSplashWindow.BackgroundDecodeWidth(sourceWidth, sourceHeight));
    }

    [Fact]
    public void TheBackgroundDecodeWidthBoundsAPortraitSourceTheWidthCapWouldMiss()
    {
        // 2000x20000 is inside every ImageHeader limit and already under 2560 wide, so
        // a width-only cap never scaled it: 40 MP, ~160 MB, allocated on the boot path.
        var width = BootSplashWindow.BackgroundDecodeWidth(2000, 20000);

        Assert.True(width < 2000, $"a tall source must be scaled down, got {width}");
        Assert.True(
            DecodedPixels(2000, 20000) <= BackgroundPixelBudget + width,
            $"decode of {width} px wide exceeds the budget");
    }

    [Theory]
    [InlineData(800, 600)]      // Small landscape.
    [InlineData(100, 2000)]     // Small but tall.
    [InlineData(1280, 800)]     // Exactly WSGM's floor.
    [InlineData(2023, 2023)]    // Square, the largest that still fits the budget exactly.
    public void TheBackgroundDecodeIsNeverUpscaled(int sourceWidth, int sourceHeight)
    {
        // The bound is an upper limit only: when it does not fall below the source's
        // own width, TryLoadBitmap skips DecodeToWidth entirely.
        Assert.True(
            BootSplashWindow.BackgroundDecodeWidth(sourceWidth, sourceHeight) >= sourceWidth,
            "a source inside the budget must not be scaled at all");
        Assert.Equal((long)sourceWidth * sourceHeight, DecodedPixels(sourceWidth, sourceHeight));
    }

    [Theory]
    [InlineData(ImageHeader.MaxDimension, 4000)]                 // Widest allowed, 80 MP.
    [InlineData(4000, ImageHeader.MaxDimension)]                 // Tallest allowed, 80 MP.
    [InlineData(ImageHeader.MaxDimension, 1)]                    // Most extreme landscape ratio.
    [InlineData(1, ImageHeader.MaxDimension)]                    // Most extreme portrait ratio.
    [InlineData(8944, 8944)]                                     // Largest allowed square.
    public void TheBackgroundDecodeStaysInsideTheBudgetAtTheImageHeaderLimits(
        int sourceWidth, int sourceHeight)
    {
        // budget * 20000 is ~8.2e10: the arithmetic must not wrap into a negative or
        // absurd width anywhere in the range ImageHeader admits.
        var width = BootSplashWindow.BackgroundDecodeWidth(sourceWidth, sourceHeight);

        Assert.InRange(width, 1, 2560);
        Assert.True(
            DecodedPixels(sourceWidth, sourceHeight) <= BackgroundPixelBudget + width,
            $"{sourceWidth}x{sourceHeight} decodes to {DecodedPixels(sourceWidth, sourceHeight)} px");
    }

    [Fact]
    public void TheBackgroundDecodeWidthFallsBackToTheWidthCapOnUnusableDimensions()
    {
        // ImageHeader gates these out before the bound is consulted; the helper still
        // must not divide by zero or return a nonsense width.
        Assert.Equal(2560, BootSplashWindow.BackgroundDecodeWidth(0, 1080));
        Assert.Equal(2560, BootSplashWindow.BackgroundDecodeWidth(1920, 0));
        Assert.Equal(2560, BootSplashWindow.BackgroundDecodeWidth(-1920, -1080));
    }
}
