using System;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     How large the splash decodes its background and logo images. Splash images can come from an
///     imported <c>.wsgmsplash</c> theme, so a small file may declare an enormous size; these bounds keep
///     the boot path's pixel buffers small whatever the source declares. Decodes only ever scale down.
/// </summary>
internal static class SplashDecode
{
    /// <summary>
    ///     DPI headroom in the logo's decode cap. <c>LogoMaxSize</c> is in DIPs, the renderer draws in
    ///     physical pixels and no DPI is known at decode time, so the cap covers the largest display
    ///     scale WSGM supports (500%, see <see cref="DisplayScale" />).
    /// </summary>
    private const int LogoDpiHeadroom = 5;

    /// <summary>Widest background decode, in physical pixels: the largest supported panel, 2560x1600.</summary>
    private const int BackgroundMaxWidth = 2560;

    /// <summary>
    ///     Output pixels any splash element may decode to: a full cover of the largest supported panel.
    ///     An area bound, because a width cap alone lets a tall source decode whole.
    /// </summary>
    private const int PixelCeiling = BackgroundMaxWidth * 1600;

    /// <summary>The logo's largest decoded edge, in pixels, for its bound in DIPs.</summary>
    /// <param name="logoMaxSizeDips">The configured logo bound in DIPs.</param>
    /// <returns>The bound times the DPI headroom, clamped to <see cref="ImageHeader.MaxDimension" />.</returns>
    internal static int LogoCap(int logoMaxSizeDips)
    {
        return (int)Math.Min((long)Math.Max(1, logoMaxSizeDips) * LogoDpiHeadroom, ImageHeader.MaxDimension);
    }

    /// <summary>The logo's output pixel budget: the area of its cap square, at most the cover ceiling.</summary>
    /// <param name="logoMaxSizeDips">The configured logo bound in DIPs.</param>
    /// <returns>The largest number of pixels the logo decode may produce.</returns>
    internal static long LogoPixelBudget(int logoMaxSizeDips)
    {
        var cap = (long)LogoCap(logoMaxSizeDips);
        return Math.Min(cap * cap, PixelCeiling);
    }

    /// <summary>
    ///     The logo's decode width bound for a source of the declared size. The rendered longer edge
    ///     lands on the cap (a portrait source scales its width by the aspect ratio), and the area stays
    ///     inside <see cref="LogoPixelBudget" />: <c>sqrt(budget * width / height)</c> is the width at
    ///     which an aspect-preserving decode produces exactly the budget. Computed in double, because
    ///     the products overflow 32 bits at the header limits.
    /// </summary>
    /// <param name="logoMaxSizeDips">The configured logo bound in DIPs.</param>
    /// <param name="sourceWidth">The width the source's header declares.</param>
    /// <param name="sourceHeight">The height the source's header declares.</param>
    /// <returns>The decode width bound in pixels, at least 1.</returns>
    internal static int LogoWidth(int logoMaxSizeDips, int sourceWidth, int sourceHeight)
    {
        var cap = LogoCap(logoMaxSizeDips);
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return cap;
        }

        var byEdge = sourceHeight > sourceWidth
            ? Math.Ceiling((double)cap * sourceWidth / sourceHeight)
            : cap;
        var byArea = Math.Floor(Math.Sqrt((double)LogoPixelBudget(logoMaxSizeDips) * sourceWidth / sourceHeight));
        return Math.Max(1, (int)Math.Min(byEdge, byArea));
    }

    /// <summary>
    ///     The background's decode width bound for a source of the declared size: the 2560 px width cap,
    ///     or the width at which the decode fills the cover ceiling, whichever is smaller. Landscape
    ///     sources hit the width cap first; tall ones are bounded by area.
    /// </summary>
    /// <param name="sourceWidth">The width the source's header declares.</param>
    /// <param name="sourceHeight">The height the source's header declares.</param>
    /// <returns>The decode width bound in pixels, at least 1.</returns>
    internal static int BackgroundWidth(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return BackgroundMaxWidth;
        }

        var byArea = Math.Sqrt((double)PixelCeiling * sourceWidth / sourceHeight);
        return Math.Max(1, (int)Math.Min(BackgroundMaxWidth, Math.Floor(byArea)));
    }

    /// <summary>
    ///     The width to decode a source to, or null to decode it at its own size. A bound is an upper
    ///     limit only: a source no wider than it is never stretched up.
    /// </summary>
    /// <param name="bound">The element's decode width bound.</param>
    /// <param name="sourceWidth">The width the source's header declares.</param>
    /// <returns>The scaled decode width, or null for a whole decode.</returns>
    internal static int? ScaledWidth(int bound, int sourceWidth)
    {
        var width = Math.Max(1, bound);
        return sourceWidth <= width ? null : width;
    }

    /// <summary>The pixels a decode produces under <see cref="ScaledWidth" />, rounding height up.</summary>
    /// <param name="bound">The element's decode width bound.</param>
    /// <param name="sourceWidth">The width the source's header declares.</param>
    /// <param name="sourceHeight">The height the source's header declares.</param>
    /// <returns>The decoded pixel count.</returns>
    internal static long DecodedPixels(int bound, int sourceWidth, int sourceHeight)
    {
        if (ScaledWidth(bound, sourceWidth) is not { } width)
        {
            return (long)sourceWidth * sourceHeight;
        }

        return width * (long)Math.Ceiling((double)width * sourceHeight / sourceWidth);
    }
}
