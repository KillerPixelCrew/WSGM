using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using WSGM.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class GameLibraryView
{
    private string _openEntryName = "";
    private IReadOnlyList<string> _reviewOrder = [];
    private int _storageShown = 48;

    /// <summary>Opens one title from a list, remembering the list so a removal can land on a neighbour.</summary>
    /// <param name="id">The title.</param>
    /// <param name="order">The titles the list showed, in its order.</param>
    private void OpenEntry(string id, IReadOnlyList<string> order)
    {
        _reviewOrder = order;
        Navigate(() => RenderEntry(id));
    }

    /// <summary>
    ///     Leaves a title's level once the title is no longer listed, such as after its files were deleted
    ///     and its shortcut removed, and focuses the nearest title the list still has.
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
            // Cards and rows carry the tags the review and storage levels give them.
            PagedRows.FocusAdded(this, (_grid ? "item:" : "entry:") + neighbour);
            PagedRows.FocusAdded(this, "storage.entry:" + neighbour);
        }
    }

    private void RenderStorageLibraries()
    {
        var stack = NewStack("Storage libraries");
        if (_service?.ReadState() is not { } state)
        {
            SetContent(stack);
            return;
        }

        AddStatus(stack, state);
        stack.Children.Add(Caption("Games on removable storage stay listed while it is disconnected and "
                                   + "return when it is inserted again. A title whose files were deleted is "
                                   + "removed from Steam automatically."));
        var storages = state.StorageLibraries ?? [];
        stack.Children.Add(Tagged(Row("Recheck all storage", "Check every connected library's files again",
            Icons.Restart, state.Loading || storages.Count == 0
                ? null
                : () => Run(token => _service.RecheckAvailabilityAsync("", token))), "storage.recheck-all"));
        foreach (var storage in storages)
        {
            var id = storage.Id;
            stack.Children.Add(Tagged(Row(storage.Name, GameLibraryRows.StorageLine(storage, state.Entries),
                GameLibraryRows.StorageNeedsAttention(storage) ? Icons.Info : Icons.SdCard,
                () =>
                {
                    _storageShown = 48;
                    Navigate(() => RenderStorageLibrary(id));
                }), "storage:" + id));
        }

        if (storages.Count == 0)
        {
            stack.Children.Add(Caption("No storage libraries yet. Import games from removable storage to "
                                       + "keep track of them here."));
        }

        AddApplyRow(stack, state);
        SetContent(stack);
    }

    private void RenderStorageLibrary(string id)
    {
        if (_service?.ReadState() is not { } state
            || state.StorageLibraries?.FirstOrDefault(storage => storage.Id == id) is not { } storage)
        {
            Back();
            Toast("That storage library is no longer listed.");
            return;
        }

        var stack = NewStack(storage.Name);
        AddStatus(stack, state);
        stack.Children.Add(Caption(GameLibraryRows.StorageLine(storage, state.Entries)));
        if (storage.Detail.Length > 0)
        {
            stack.Children.Add(Caption(storage.Detail));
        }

        var busy = state.Loading;
        if (storage.StagedAction.Length > 0)
        {
            stack.Children.Add(Tagged(Row(storage.StagedAction == "repair" ? "Don't repair" : "Don't use this card",
                GameLibraryRows.StagedStorageAction(storage.StagedAction) + ". Leave it out of the next apply",
                Icons.Close,
                busy ? null : () => Run(token => _service.StageStorageActionAsync(id, "", token))), "storage.action"));
        }
        else if (storage.CanRepair)
        {
            stack.Children.Add(Tagged(Row("Repair library",
                "Rebuild this storage's game list from the titles WSGM already knows. Done when you apply",
                Icons.Wrench,
                busy ? null : () => Run(token => _service.StageStorageActionAsync(id, "repair", token))),
                "storage.action"));
        }
        else if (storage.CanAcceptBinding)
        {
            stack.Children.Add(Tagged(Row("Use this card",
                "Launch this library's titles from the storage now connected. Done when you apply",
                Icons.SdCard,
                busy ? null : () => Run(token => _service.StageStorageActionAsync(id, "acceptBinding", token))),
                "storage.action"));
        }

        AddApplyRow(stack, state);
        stack.Children.Add(Tagged(Row("Recheck", "Check this library's files again", Icons.Restart,
            busy ? null : () => Run(token => _service.RecheckAvailabilityAsync("library:" + id, token))),
            "storage.recheck"));

        var titles = state.Entries.Where(entry => entry.LibraryId == id).ToArray();
        stack.Children.Add(SectionLabel($"TITLES ({titles.Length})"));
        var order = titles.Select(entry => entry.Id).ToArray();
        foreach (var entry in titles.Take(_storageShown))
        {
            var entryId = entry.Id;
            stack.Children.Add(Tagged(Row(entry.Name, GameLibraryRows.Describe(entry),
                GameLibraryRows.IsUnavailable(entry) || entry.CleanupPending ? Icons.BlockedCircle : null,
                () => OpenEntry(entryId, order)), "storage.entry:" + entryId));
        }

        if (titles.Length > _storageShown)
        {
            var added = "storage.entry:" + titles[_storageShown].Id;
            stack.Children.Add(Tagged(Row("Load more", $"{_storageShown} of {titles.Length}", Icons.ArrowDown, () =>
            {
                _storageShown += 48;
                RenderStorageLibrary(id);
                PagedRows.FocusAdded(this, added);
            }), "storage.more"));
        }

        if (titles.Length == 0)
        {
            stack.Children.Add(Caption(storage.TitleCount > 0
                ? "Scan to list this library's titles."
                : "No titles from this library are listed."));
        }

        SetContent(stack);
    }

    /// <summary>A title's storage: why it is being removed, and the way to its library.</summary>
    private void AddStorageEntryControls(StackPanel body, GameLibraryState state, GameLibraryEntry entry)
    {
        if (entry.CleanupPending)
        {
            body.Children.Add(Caption("Its game files were deleted. WSGM removes its Steam shortcut as soon as "
                                      + "Steam allows; your other files and saves stay."));
        }

        if (entry.LibraryId.Length == 0
            || state.StorageLibraries?.FirstOrDefault(storage => storage.Id == entry.LibraryId) is not { } storage)
        {
            return;
        }

        var id = storage.Id;
        body.Children.Add(Tagged(Row("Storage: " + storage.Name, storage.Status, Icons.SdCard, () =>
        {
            _storageShown = 48;
            Navigate(() => RenderStorageLibrary(id));
        }), "entry.storage"));
    }

    /// <summary>Adds the shared Apply row when the review has staged work, including staged storage actions.</summary>
    private void AddApplyRow(StackPanel stack, GameLibraryState state)
    {
        if (state.SelectedCount > 0)
        {
            stack.Children.Add(Tagged(PrimaryRow($"Apply {state.SelectedCount}",
                state.LauncherAvailable ? "Save the selected changes" : state.LauncherDetail ?? string.Empty,
                Icons.Play, state.Loading ? null : () => Run(_service!.ApplyAsync)), "apply"));
        }
    }
}
