using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace WSGM.Core;

/// <summary>
///     Offers the resolutions the driver accepts and applies the one the user chooses.
/// </summary>
/// <remarks>
///     Separate from <see cref="RefreshRatePairingService" /> even though both move the display, because
///     they are owned by different things: the pairing service moves the refresh rate as a consequence
///     of a frame cap and puts it back, while this moves the resolution because the user asked it to.
///     The choice is saved, as Windows Settings saves it, and nothing puts it back. Restoring the mode
///     found before the first change undid the user's own choice when WSGM exited, and a dynamic change
///     ended at the next restart, so the display kept returning to an old saved resolution (an Ally X
///     at 1120x630 on every boot, 2026-10-20).
///     <para>
///         Discovery is cached until the display changes, because enumerating and <c>CDS_TEST</c>-ing every mode is
///         not something to repeat while a menu is open.
///     </para>
/// </remarks>
internal sealed class DisplayResolutionService
{
    private readonly Func<int, int, bool> _apply;
    private readonly Func<IReadOnlyList<DisplayResolution>> _discover;
    private readonly Lock _gate = new();
    private IReadOnlyList<DisplayResolution>? _accepted;
    private long _acceptedRevision = -1;
    private long _displayRevision;

    /// <summary>Creates the service against the real display.</summary>
    internal DisplayResolutionService()
        : this(
            PrimaryDisplayModes.EnumerateAcceptedResolutions,
            PrimaryDisplayModes.TryApplyResolution)
    {
    }

    /// <summary>Creates the service against supplied display operations.</summary>
    /// <param name="discover">Reads the resolutions the driver accepts.</param>
    /// <param name="apply">Applies and saves one, reporting whether it took.</param>
    /// <remarks>Both are injected, so the service can be tested without a display.</remarks>
    internal DisplayResolutionService(
        Func<IReadOnlyList<DisplayResolution>> discover,
        Func<int, int, bool> apply)
    {
        _discover = discover ?? throw new ArgumentNullException(nameof(discover));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
    }

    /// <summary>The resolutions worth offering.</summary>
    /// <returns>Accepted resolutions, ascending by pixel count.</returns>
    internal IReadOnlyList<DisplayResolution> Options()
    {
        lock (_gate)
        {
            var revision = Interlocked.Read(ref _displayRevision);
            if (_accepted is null || _acceptedRevision != revision)
            {
                _accepted = _discover();
                _acceptedRevision = revision;
            }

            return _accepted;
        }
    }

    /// <summary>Invalidates mode discovery after a display signal without blocking its UI thread.</summary>
    internal void InvalidateOptions()
    {
        Interlocked.Increment(ref _displayRevision);
    }

    /// <summary>Applies and saves a resolution.</summary>
    /// <param name="resolution">The resolution to apply.</param>
    /// <returns>Whether the display is now at that resolution.</returns>
    /// <remarks>
    ///     Refuses anything discovery did not accept rather than passing it to the driver. A resolution
    ///     that was never validated is one the panel may not display at all, and recovering from a mode
    ///     the user cannot see is not something to leave them to do.
    /// </remarks>
    internal bool Apply(DisplayResolution resolution)
    {
        if (!Options().Contains(resolution))
        {
            Log.Warn(
                $"Display resolution {resolution} refused: it is not among the accepted modes "
                + $"[{string.Join(",", Options())}].");
            return false;
        }

        return _apply(resolution.Width, resolution.Height);
    }
}
