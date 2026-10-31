using System.Linq;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>
///     WSGM's instruction to Big Picture Home's carousel: which games are on a library that is not
///     attached, and whether uninstalled games are wanted.
/// </summary>
/// <remarks>
///     Built from the same card reading as the library badge (<see cref="LibraryBadges" />), so the
///     carousel and the badge can never disagree about which library is attached. Steam's own installed
///     flag already drops a pulled card's games; the list here is what makes that authoritative in the
///     moment before Steam catches up, and it is WSGM's card model rather than Steam's that decides.
/// </remarks>
internal static class HomeCarousel
{
    /// <summary>The instruction for one card reading.</summary>
    /// <param name="libraries">The tracked libraries, or null before the card model was read once.</param>
    /// <param name="includeUninstalled">Whether owned games that are not installed are listed.</param>
    /// <returns>
    ///     The app ids on a disconnected library, minus any that an attached library also holds, in
    ///     ascending order so an unchanged reading publishes identically.
    /// </returns>
    internal static SteamHomeCarouselState Build(SteamLibraryBadgeState? libraries, bool includeUninstalled)
    {
        if (libraries is null)
        {
            return new SteamHomeCarouselState(includeUninstalled, []);
        }

        var attached = libraries.Libraries
            .Where(static library => library.Connected)
            .SelectMany(static library => library.AppIds)
            .ToHashSet();
        var disconnected = libraries.Libraries
            .Where(static library => !library.Connected)
            .SelectMany(static library => library.AppIds)
            .Where(appId => !attached.Contains(appId))
            .Distinct()
            .Order()
            .ToArray();
        return new SteamHomeCarouselState(includeUninstalled, disconnected, libraries.Revision);
    }
}
