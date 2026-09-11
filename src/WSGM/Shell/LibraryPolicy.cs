using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>What the registrations at one card path need.</summary>
internal enum LibraryTransition
{
    /// <summary>Steam's view already matches the card that is in the reader.</summary>
    None,

    /// <summary>Registrations exist for a library that is not on this volume any
    /// more; remove them and add nothing.</summary>
    Purge,

    /// <summary>Registrations exist for a DIFFERENT card, and the card now in the
    /// reader carries its own library; replace them.</summary>
    Replace,

    /// <summary>The card carries a library Steam does not know about; add it.</summary>
    Add,
}

/// <summary>
/// The one owner of whether a removable library is registered with Steam.
/// </summary>
/// <remarks>
/// Adopt, eject and format are the only transitions, which is Steam's own storage model rather
/// than a second one beside it. Everything that can change a removable library's registration goes
/// through here: the overlay's eject panel, Steam's revived storage pages, the format flow, and
/// volume arrival and departure. Detection stays with <see cref="CardVolumeMonitor" />, which
/// reports what it saw and no longer decides anything.
/// <para>
/// The reason for one owner is a fault, not tidiness. The monitor treated "a library is on a
/// mounted volume and Steam does not list it" as sufficient reason to register it. A media-level
/// eject leaves the card physically in the reader, Windows remounts it within seconds, and the
/// monitor put back the registration the user had just ejected — from either surface, since both
/// end at the same physical eject. An eject is now an intent that outlives the remount, and only a
/// card actually leaving, or an explicit adopt, clears it.
/// </para>
/// </remarks>
internal sealed class LibraryPolicy : IRemovableDriveEjectObserver
{
    /// <summary>Volume roots ejected on purpose, with the library identity that was on them.</summary>
    /// <remarks>
    /// Keyed by volume root rather than library path because the eject surfaces name a drive, not
    /// a folder. The value is the content id that was ejected: a different card in the same reader
    /// is a different library and must register normally, so the intent is matched on identity and
    /// not on the slot.
    /// </remarks>
    private readonly Dictionary<string, string> _ejected = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <inheritdoc />
    /// <remarks>
    /// Unregistering happens here, before the media goes, and the intent is recorded with it. Both
    /// are undone by <see cref="EjectedAsync" /> when Windows refuses the eject, because a card
    /// that never went anywhere must not be left out of Steam's list.
    /// </remarks>
    public async Task EjectingAsync(RemovableDriveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        foreach (string libraryPath in LibraryPathsOn(entry))
        {
            string contentId = ReadContentId(libraryPath);
            NoteEjected([libraryPath], contentId);
            if (contentId.Length == 0)
            {
                continue;
            }

            Log.Info($"Library policy: unregistering {libraryPath} before ejecting {entry.Name}.");
            try
            {
                await UnregisterAsync(libraryPath, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A client that would not take the removal is not a reason to refuse the eject:
                // the card is the user's to remove, and the monitor's departure pass cleans up.
                Log.Warn($"Library policy: Steam would not drop {libraryPath}: {ex.Message}");
            }
        }
    }

    /// <inheritdoc />
    public Task EjectedAsync(RemovableDriveEntry entry, bool succeeded)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (succeeded)
        {
            return Task.CompletedTask;
        }

        // Refused. The card is still mounted and still the user's library, so the intent is
        // dropped and the monitor's next pass registers it again.
        foreach (string libraryPath in LibraryPathsOn(entry))
        {
            ClearEjected(libraryPath);
        }

        return Task.CompletedTask;
    }

    /// <summary>The card library paths that would sit on one row's volumes.</summary>
    /// <param name="entry">The row being ejected.</param>
    /// <returns>One path per mounted letter, in the layout the card scan uses.</returns>
    private static IReadOnlyList<string> LibraryPathsOn(RemovableDriveEntry entry) =>
        SteamStorageBridge.SplitLetters(entry.Letters)
            .Select(path => System.IO.Path.Combine(path, "SteamLibrary"))
            .ToArray();

    /// <summary>The library identity at a path, or empty when it carries none.</summary>
    /// <param name="libraryPath">The card library path.</param>
    /// <returns>The content id, or an empty string.</returns>
    private static string ReadContentId(string libraryPath)
    {
        try
        {
            return SteamLibraryVdf.TryReadMarker(libraryPath, out string? id, out _)
                ? id ?? ""
                : "";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An unreadable marker on a card about to leave is not worth failing an eject over.
            Log.Warn($"Library policy: could not read the library marker at {libraryPath}: "
                + ex.Message);
            return "";
        }
    }

    /// <summary>Records that the library on a volume was ejected deliberately.</summary>
    /// <param name="mountPaths">The volume's mount paths, as the eject surface knows them.</param>
    /// <param name="contentId">The library identity on it, or empty when it carried none.</param>
    internal void NoteEjected(IReadOnlyList<string> mountPaths, string contentId)
    {
        ArgumentNullException.ThrowIfNull(mountPaths);
        lock (_gate)
        {
            foreach (string path in mountPaths)
            {
                string root = RootOf(path);
                if (root.Length > 0)
                {
                    _ejected[root] = contentId ?? "";
                    Log.Info($"Library policy: {root} ejected on purpose"
                        + $"{(contentId is { Length: > 0 } ? $" (library {contentId})" : "")}; "
                        + "it will not be re-registered until the card is replaced or adopted.");
                }
            }
        }
    }

    /// <summary>Forgets a standing eject intent for a volume.</summary>
    /// <param name="path">Any path on the volume.</param>
    /// <remarks>
    /// Called when the media actually leaves and when the user adopts the volume again. Both mean
    /// the intent has been served or overridden, and a remount after either is an ordinary insert.
    /// </remarks>
    internal void ClearEjected(string path)
    {
        string root = RootOf(path);
        if (root.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_ejected.Remove(root))
            {
                Log.Info($"Library policy: {root} is no longer held ejected.");
            }
        }
    }

    /// <summary>Whether a library identity on a volume is being held out of Steam's list.</summary>
    /// <param name="path">The library path observed on the volume.</param>
    /// <param name="contentId">The identity its marker carries, or null when it has none.</param>
    /// <returns>True while the standing eject covers exactly that library.</returns>
    internal bool IsHeldEjected(string path, string? contentId)
    {
        string root = RootOf(path);
        if (root.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_ejected.TryGetValue(root, out string? ejectedId))
            {
                return false;
            }

            // A blank remembered id means the volume carried no library when it was ejected, so
            // anything appearing now is new and registers normally.
            if (ejectedId.Length == 0 || contentId is null or { Length: 0 })
            {
                return false;
            }

            if (string.Equals(ejectedId, contentId, StringComparison.Ordinal))
            {
                return true;
            }

            // A different card in the same slot. The intent was about the one that left.
            _ejected.Remove(root);
            Log.Info($"Library policy: {root} now holds a different library ({contentId}); "
                + "the standing eject no longer applies.");
            return false;
        }
    }

    /// <summary>Decides what the registrations at one path need.</summary>
    /// <param name="cardContentId">The identity the card's marker carries, or null for none.</param>
    /// <param name="registeredContentIds">What Steam has registered at that path.</param>
    /// <returns>The transition to apply.</returns>
    internal static LibraryTransition Decide(
        string? cardContentId, IReadOnlyCollection<string> registeredContentIds)
    {
        ArgumentNullException.ThrowIfNull(registeredContentIds);
        var registered = registeredContentIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();

        if (string.IsNullOrWhiteSpace(cardContentId))
        {
            // Nothing on the volume claims to be a Steam library, so anything Steam
            // still lists at this path belongs to a card that has left the reader.
            return registered.Count > 0 ? LibraryTransition.Purge : LibraryTransition.None;
        }
        if (registered.Count == 0)
        {
            return LibraryTransition.Add;
        }
        // Exactly the one registration, and it is this card's: leave it alone. Any
        // other shape - a different id, or this id sitting next to a stale duplicate -
        // has to be rebuilt, because Steam offers no way to drop just one of them by
        // identity.
        return registered.Count == 1
            && string.Equals(registered[0], cardContentId, StringComparison.Ordinal)
                ? LibraryTransition.None
                : LibraryTransition.Replace;
    }

    /// <summary>Applies one transition through Steam's own front end.</summary>
    /// <param name="transition">What the registrations at this path need.</param>
    /// <param name="libraryPath">The card library, for example <c>E:\SteamLibrary</c>.</param>
    /// <param name="cardLabel">
    /// The label the card's own marker carries, empty when it has none. Passed on an add because
    /// Steam's label belongs to the PATH registration, not the card: re-registering a reader path
    /// leaves the previous card's label in place, and Steam's storage page then names this card
    /// after the last one. An empty label is left as null so Steam keeps its own default.
    /// </param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>Whether Steam's list changed.</returns>
    internal static async Task<bool> ApplyAsync(
        LibraryTransition transition, string libraryPath, string cardLabel,
        CancellationToken cancellationToken)
    {
        if (transition == LibraryTransition.Purge)
        {
            var removal = await SteamCdp.RemoveLibrariesAtPathAsync(libraryPath, cancellationToken)
                .ConfigureAwait(false);
            return removal.Status == SteamLibraryRemoveStatus.Removed;
        }

        // Replace and Add both end in an add. `replaceExisting` makes the add drop
        // whatever is registered at the path first, which is exactly Replace; for Add
        // there is nothing there to drop, so one call covers both.
        var add = await SteamCdp.AddLibraryAsync(
            libraryPath,
            label: string.IsNullOrWhiteSpace(cardLabel) ? null : cardLabel,
            replaceExisting: transition == LibraryTransition.Replace,
            cancellationToken).ConfigureAwait(false);
        return add.Status is SteamLibraryAddStatus.Added or SteamLibraryAddStatus.AlreadyPresent;
    }

    /// <summary>Unregisters the library on a volume, as the step before a deliberate eject.</summary>
    /// <param name="libraryPath">The library to unregister.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>Whether Steam's list changed.</returns>
    /// <remarks>
    /// Ordered before the physical eject on purpose. Ejecting first leaves Steam holding a library
    /// on a volume that is gone, which is the state its own UI renders as a disconnected drive and
    /// which the monitor then has to clean up on a later pass.
    /// </remarks>
    internal static Task<bool> UnregisterAsync(
        string libraryPath, CancellationToken cancellationToken) =>
        ApplyAsync(LibraryTransition.Purge, libraryPath, "", cancellationToken);

    /// <summary>The volume root a path sits on, upper-cased, for example <c>D:\</c>.</summary>
    /// <param name="path">Any path on the volume.</param>
    /// <returns>The root, or empty when the path does not name one.</returns>
    private static string RootOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        try
        {
            return System.IO.Path.GetPathRoot(path) ?? "";
        }
        catch (ArgumentException)
        {
            return "";
        }
    }
}
