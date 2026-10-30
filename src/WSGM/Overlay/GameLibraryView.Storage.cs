using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using WSGM.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class GameLibraryView
{
    private string _openEntryName = "";
    private IReadOnlyList<string> _reviewOrder = [];
    private Func<string, string>? _reviewTag;
    private int _storageShown = 48;

    /// <summary>Opens one title from a list, remembering the list so a removal can land on a neighbour.</summary>
    /// <param name="id">The title.</param>
    /// <param name="order">The titles the list showed, in its order.</param>
    /// <param name="tagOf">The focus tag that list gives a title, or null for the review's own cards and rows.</param>
    private void OpenEntry(string id, IReadOnlyList<string> order, Func<string, string>? tagOf = null)
    {
        _reviewOrder = order;
        _reviewTag = tagOf;
        Navigate(() => RenderEntry(id));
    }

    /// <summary>
    ///     Leaves a title's level once the title is no longer listed, such as after its shortcut was removed,
    ///     and focuses the nearest title the list still has.
    /// </summary>
    /// <param name="id">The title that went away.</param>
    /// <param name="state">The current state.</param>
    private void LeaveRemovedEntry(string id, GameLibraryState state)
    {
        var listed = state.Entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var index = _reviewOrder.ToList().IndexOf(id);
        var neighbour = index < 0
            ? _reviewOrder.FirstOrDefault(listed.Contains)
            : _reviewOrder.Skip(index + 1).FirstOrDefault(listed.Contains)
              ?? _reviewOrder.Take(index).LastOrDefault(listed.Contains);
        var name = _openEntryName;
        Back();
        Toast(name.Length > 0 ? $"{name} is no longer listed." : "That title is no longer listed.");
        if (neighbour is not null)
        {
            PagedRows.FocusAdded(this, _reviewTag?.Invoke(neighbour) ?? (_grid ? "item:" : "entry:") + neighbour);
        }
    }

    /// <summary>Holds a title's level while a scan or apply has taken it out of the list for now.</summary>
    /// <remarks>
    ///     The progress updates in place; a published state that brings the title back or ends the work renders the
    ///     title's level again, with the title or without it.
    /// </remarks>
    /// <param name="id">The title.</param>
    /// <param name="shown">The reading without the title.</param>
    private void RenderEntryPending(string id, EntryShown shown)
    {
        var stack = NewStack(_openEntryName.Length > 0 ? _openEntryName : "Title");
        AddStatus(stack, shown.State);
        stack.Children.Add(Caption("The library is being updated. This title returns when the update "
                                   + "finishes, unless it was removed."));
        SetEntryContent(stack, id, shown);
    }

    /// <summary>
    ///     Every non-Steam shortcut, grouped by the card, disk or drive its files are on, with each card library's
    ///     immediate Repair or Use this card action and one Recheck.
    /// </summary>
    private void RenderStorage()
    {
        var stack = NewStack("Storage");
        if (_service?.ReadState() is not { } state)
        {
            SetContent(stack);
            return;
        }

        AddStatus(stack, state);
        stack.Children.Add(Caption("Every non-Steam shortcut in Steam, grouped by where its files are. WSGM shows "
                                   + "when a card is out or files are missing, and never removes a shortcut itself."));
        var shortcuts = state.Shortcuts ?? [];
        var storages = state.StorageLibraries ?? [];
        stack.Children.Add(Tagged(Row("Recheck", state.StorageChecked
                ? "Check every shortcut and card again"
                : "Checking shortcuts for the first time",
            Icons.Restart, state.Loading ? null : () => Run(token => _service.RecheckAvailabilityAsync(token))),
            "storage.recheck"));

        var groups = GameLibraryRows.StorageGroups(shortcuts, storages);
        if (groups.Count == 0)
        {
            stack.Children.Add(Caption(state.StorageChecked
                ? "No shortcut in Steam has its files on this device's storage, and no game library card is known."
                : "Shortcuts are listed here once Steam has been checked."));
            SetLibraryContent(stack, state, SameStorage);
            return;
        }

        var entries = state.Entries.Where(entry => entry.AppId > 0).GroupBy(entry => entry.AppId)
            .ToDictionary(group => group.Key, group => group.First().Id);
        var tags = entries.ToDictionary(pair => pair.Value, pair => "storage.shortcut:" + pair.Key, StringComparer.Ordinal);
        var order = groups.SelectMany(group => group.Shortcuts).Select(shortcut => entries.GetValueOrDefault(shortcut.AppId))
            .OfType<string>().ToArray();
        var shown = 0;
        string? more = null;
        foreach (var group in groups)
        {
            var unavailable = group.Shortcuts.Count(shortcut => !shortcut.Available);
            var heading = SectionLabel(group.Shortcuts.Count == 0 ? group.Name.ToUpperInvariant()
                : unavailable > 0
                    ? $"{group.Name.ToUpperInvariant()} ({group.Shortcuts.Count}, {unavailable} UNAVAILABLE)"
                    : $"{group.Name.ToUpperInvariant()} ({group.Shortcuts.Count})");
            // A card's own name can be long; it wraps rather than clipping at the pane's edge.
            heading.TextWrapping = TextWrapping.Wrap;
            stack.Children.Add(heading);
            if (group.Library is { } library)
            {
                AddLibraryRows(stack, library, group.Shortcuts.Count > 0, state.Loading);
            }

            foreach (var shortcut in group.Shortcuts)
            {
                if (shown == _storageShown)
                {
                    more ??= "storage.shortcut:" + shortcut.AppId;
                    break;
                }

                shown++;
                var appId = shortcut.AppId;
                Action open = entries.TryGetValue(appId, out var entryId)
                    ? () => OpenEntry(entryId, order, id => tags.GetValueOrDefault(id, "storage.recheck"))
                    : () => Toast(shortcut.Available
                        ? $"{shortcut.Name} is available on {shortcut.Location}."
                        : $"{shortcut.Name}: {shortcut.Detail}");
                stack.Children.Add(Tagged(Row(shortcut.Name, GameLibraryRows.ShortcutLine(shortcut),
                    shortcut.Available ? null : Icons.BlockedCircle, open), "storage.shortcut:" + appId));
            }

            if (more is not null)
            {
                break;
            }
        }

        if (more is { } next)
        {
            var total = shortcuts.Count;
            stack.Children.Add(Tagged(Row("Load more", $"{shown} of {total}", Icons.ArrowDown, () =>
            {
                _storageShown += 48;
                RenderStorage();
                PagedRows.FocusAdded(this, next);
            }), "storage.more"));
        }

        SetLibraryContent(stack, state, SameStorage);
    }

    /// <summary>A card library's status and, when it has one, its immediate action.</summary>
    private void AddLibraryRows(StackPanel stack, GameLibraryStorage library, bool listed, bool busy)
    {
        var id = library.Id;
        stack.Children.Add(Tagged(Row(GameLibraryRows.StorageLine(library),
            library.Detail.Length > 0 ? library.Detail
            : listed ? "Its shortcuts in Steam are listed below."
            : "No shortcut in Steam uses this card. Scan to import its games.",
            GameLibraryRows.StorageNeedsAttention(library) ? Icons.Info : Icons.SdCard,
            () => Toast(library.Name + ": " + GameLibraryRows.StorageLine(library))), "storage.library:" + id));
        if (library.CanRepair)
        {
            stack.Children.Add(Tagged(Row("Repair library",
                "Rebuild this card's game list from the titles WSGM knows. Your game files stay as they are",
                Icons.Wrench, busy ? null : () => Run(token => _service!.RepairLibraryAsync(id, token))),
                "storage.repair:" + id));
        }

        if (library.CanAcceptBinding)
        {
            stack.Children.Add(Tagged(Row("Use this card",
                "Launch this card's games on this device from where it is connected now",
                Icons.SdCard, busy ? null : () => Run(token => _service!.UseLibraryAsync(id, token))),
                "storage.use:" + id));
        }
    }

    /// <summary>A title's availability: what to do when it cannot start, and the way to the Storage page.</summary>
    private void AddStorageEntryControls(StackPanel body, GameLibraryState state, GameLibraryEntry entry)
    {
        if (entry.AppId == 0 || (state.Shortcuts ?? []).All(shortcut => shortcut.AppId != entry.AppId))
        {
            return;
        }

        body.Children.Add(Tagged(Row("Storage: " + entry.Location,
            entry.Unavailable && entry.AvailabilityDetail.Length > 0
                ? entry.AvailabilityLabel + " · " + entry.AvailabilityDetail
                : entry.AvailabilityLabel,
            entry.Unavailable ? Icons.BlockedCircle : Icons.SdCard, () =>
            {
                _storageShown = 48;
                Navigate(RenderStorage);
            }), "entry.storage"));
    }
}
