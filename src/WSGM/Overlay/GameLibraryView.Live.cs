using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>How the Game Library's levels follow the service while it scans or applies.</summary>
/// <remarks>
///     Every title an apply saves publishes the whole state again. Each level therefore registers what it draws:
///     a publication that changes none of it updates the level's progress element in place, and only one that
///     changes something the level shows draws it again. A level that adds a field to what it draws adds the same
///     field to its comparison here.
/// </remarks>
public sealed partial class GameLibraryView
{
    private static readonly IReadOnlyList<GameLibraryArtworkSlot> NoArtwork = [];
    private static readonly IReadOnlyList<string> NoNotes = [];
    private static readonly IReadOnlyList<GameLibraryArtworkOption> NoOptions = [];
    private static readonly IReadOnlyList<GameLibraryRoute> NoRoutes = [];

    /// <summary>A level's status: the scan or apply in progress with its Stop, then the error, else the notice.</summary>
    private void AddStatus(StackPanel stack, GameLibraryState state)
    {
        stack.Children.Add(new OverlayProgress("library.progress", ProgressOf(state), "stop"));
        if (state.Error is { Length: > 0 } error)
        {
            stack.Children.Add(Tagged(Caption(error), "library.message"));
        }
        else if (state.Notice is { Length: > 0 } notice)
        {
            stack.Children.Add(Tagged(Caption(notice), "library.message"));
        }
    }

    /// <summary>
    ///     The library's running work, as the Steam page shows it: indeterminate while scanning, counted while
    ///     saving, and Stop while either runs.
    /// </summary>
    private OverlayProgressState? ProgressOf(GameLibraryState state)
    {
        if (!state.Loading)
        {
            return null;
        }

        if (state.Phase == GameLibraryPhases.Scanning)
        {
            return new OverlayProgressState("Scanning…", true, null,
                new OverlayProgressCancel("Stop scan", "Stops reading the sources. Nothing is written to Steam",
                    Stop));
        }

        if (state.Phase == GameLibraryPhases.Applying)
        {
            return new OverlayProgressState($"Saving {state.Progress} of {state.ProgressTotal}…", true,
                state.ProgressTotal > 0 ? (double)state.Progress / state.ProgressTotal : null,
                new OverlayProgressCancel("Stop", "Stops after the title in progress; nothing is rolled back", Stop));
        }

        return new OverlayProgressState("Working…", true, null,
            new OverlayProgressCancel("Stop", "Stops the library's current work", Stop));

        void Stop()
        {
            Run(_service!.CancelAsync);
        }
    }

    /// <summary>Sets a level drawn from the library state alone.</summary>
    /// <param name="stack">The level.</param>
    /// <param name="state">The state it was drawn from.</param>
    /// <param name="same">Whether two states draw the same level, apart from the status every level shows.</param>
    private void SetLibraryContent(StackPanel stack, GameLibraryState state,
        Func<GameLibraryState, GameLibraryState, bool> same)
    {
        SetLibraryContent(stack, state, () => _service?.ReadState(), static shown => shown, same);
    }

    /// <summary>Sets a level drawn from the library state and further reads of the service.</summary>
    /// <typeparam name="TShown">What the level was drawn from.</typeparam>
    /// <param name="stack">The level.</param>
    /// <param name="shown">What it was drawn from, read before drawing it.</param>
    /// <param name="read">Reads the same again, or null when the title or the service is gone.</param>
    /// <param name="stateOf">The library state inside a reading.</param>
    /// <param name="same">Whether two readings draw the same level, apart from the status every level shows.</param>
    private void SetLibraryContent<TShown>(StackPanel stack, TShown shown, Func<TShown?> read,
        Func<TShown, GameLibraryState> stateOf, Func<TShown, TShown, bool> same)
        where TShown : class
    {
        SetLiveContent(stack, shown, read, (a, b) => SameStatus(stateOf(a), stateOf(b)) && same(a, b),
            next => ProgressOf(stateOf(next)));
    }

    /// <summary>
    ///     Whether two states show the same status rows and enablement: whether work runs, which disables the
    ///     levels' commands, and the message. The phase and count are the progress element's own.
    /// </summary>
    private static bool SameStatus(GameLibraryState shown, GameLibraryState next)
    {
        return shown.Loading == next.Loading && shown.Error == next.Error && shown.Notice == next.Notice;
    }

    /// <summary>Whether two states list the same sources and source settings, as the Sources level draws them.</summary>
    private static bool SameSources(GameLibraryState shown, GameLibraryState next)
    {
        return shown.Sources.SequenceEqual(next.Sources)
               && shown.CreateCollections == next.CreateCollections
               && (shown.RomSources ?? []).Select(source => (source.Id, source.Name, source.Root.AbsolutePath))
               .SequenceEqual((next.RomSources ?? []).Select(source =>
                   (source.Id, source.Name, source.Root.AbsolutePath)))
               && (shown.ManualSources ?? []).Select(source => (source.Id, source.Name, source.Target))
               .SequenceEqual((next.ManualSources ?? []).Select(source => (source.Id, source.Name, source.Target)));
    }

    /// <summary>Whether two states draw the same Storage level: its shortcuts, card libraries and title links.</summary>
    private static bool SameStorage(GameLibraryState shown, GameLibraryState next)
    {
        return shown.StorageChecked == next.StorageChecked
               && (shown.Shortcuts ?? []).SequenceEqual(next.Shortcuts ?? [])
               && (shown.StorageLibraries ?? []).SequenceEqual(next.StorageLibraries ?? [])
               && Linked(shown).SequenceEqual(Linked(next));

        static IEnumerable<(uint AppId, string Id)> Linked(GameLibraryState state)
        {
            return state.Entries.Where(entry => entry.AppId > 0).Select(entry => (entry.AppId, entry.Id));
        }
    }

    /// <summary>Whether two states draw the same review: its tabs, sources, cards and selection.</summary>
    private static bool SameReview(GameLibraryState shown, GameLibraryState next)
    {
        return shown.SelectedCount == next.SelectedCount
               && shown.Sources.Select(source => (source.Id, source.Name))
                   .SequenceEqual(next.Sources.Select(source => (source.Id, source.Name)))
               && shown.Entries.Count == next.Entries.Count
               && shown.Entries.Zip(next.Entries).All(pair => SameEntry(pair.First, pair.Second));
    }

    /// <summary>Whether two states draw the same staged-artwork overview of the selected titles.</summary>
    private static bool SameSelectedArtwork(GameLibraryState shown, GameLibraryState next)
    {
        var before = shown.Entries.Where(entry => entry.Selected).ToArray();
        var after = next.Entries.Where(entry => entry.Selected).ToArray();
        return shown.SelectedCount == next.SelectedCount && before.Length == after.Length
                                                         && before.Zip(after).All(pair =>
                                                             SameEntry(pair.First, pair.Second));
    }

    /// <summary>Whether two projections of a title are the same, including its routes, artwork and arguments.</summary>
    private static bool SameEntry(GameLibraryEntry? shown, GameLibraryEntry? next)
    {
        if (shown is null || next is null)
        {
            return shown is null && next is null;
        }

        return Flat(shown) == Flat(next)
               && shown.Routes.SequenceEqual(next.Routes)
               && shown.Artwork.SequenceEqual(next.Artwork)
               && (shown.Arguments ?? []).SequenceEqual(next.Arguments ?? []);

        static GameLibraryEntry Flat(GameLibraryEntry entry)
        {
            return entry with { Routes = NoRoutes, Artwork = NoArtwork, Arguments = null };
        }
    }

    /// <summary>Whether two readings of a title's evidence are the same.</summary>
    private static bool SameDetails(GameLibraryDetails? shown, GameLibraryDetails? next)
    {
        if (shown is null || next is null)
        {
            return shown is null && next is null;
        }

        return shown with { Notes = NoNotes } == next with { Notes = NoNotes } && shown.Notes.SequenceEqual(next.Notes);
    }

    /// <summary>Whether two readings of an artwork slot's candidates are the same.</summary>
    private static bool SameOptions(GameLibraryOptionsAnswer? shown, GameLibraryOptionsAnswer? next)
    {
        if (shown is null || next is null)
        {
            return shown is null && next is null;
        }

        return shown with { Options = NoOptions } == next with { Options = NoOptions }
               && shown.Options.SequenceEqual(next.Options);
    }

    /// <summary>Reads what a title's level draws, or null when the service is gone.</summary>
    /// <param name="id">The title.</param>
    private EntryShown? ReadEntry(string id)
    {
        if (_service is not { } service)
        {
            return null;
        }

        var state = service.ReadState();
        var entry = state.Entries.FirstOrDefault(item => item.Id == id);
        return new EntryShown(state, entry, entry is null ? null : service.ReadDetails(id), service.ReadRomState(),
            entry is { AppId: > 0 } && (state.Shortcuts ?? []).Any(shortcut => shortcut.AppId == entry.AppId));
    }

    /// <summary>Sets a title's level, or its pending level while the title is out of the list.</summary>
    /// <param name="stack">The level.</param>
    /// <param name="id">The title.</param>
    /// <param name="shown">What it was drawn from.</param>
    private void SetEntryContent(StackPanel stack, string id, EntryShown shown)
    {
        SetLibraryContent(stack, shown, () => ReadEntry(id), static reading => reading.State,
            static (a, b) => SameEntry(a.Entry, b.Entry) && SameDetails(a.Details, b.Details)
                                                         && ReferenceEquals(a.Rom, b.Rom) && a.Tracked == b.Tracked);
    }

    /// <summary>Reads what a title's staged-artwork level draws, or null when the title or service is gone.</summary>
    /// <param name="id">The title.</param>
    /// <param name="asset">The artwork slot.</param>
    private ArtworkShown? ReadArtwork(string id, string asset)
    {
        if (_service is not { } service
            || service.ReadState() is not { } state
            || state.Entries.FirstOrDefault(item => item.Id == id) is not { } entry)
        {
            return null;
        }

        return new ArtworkShown(state, entry, service.ReadArtworkOptions(id, asset));
    }

    /// <summary>Reads what the ROM source editor draws besides its draft.</summary>
    private RomEditorShown? ReadRomEditor()
    {
        return _service is { } service
            ? new RomEditorShown(service.ReadState(), service.ReadRomState(),
                service.ReadRomSystems().Select(system => (system.Id, system.Name)).ToArray())
            : null;
    }

    /// <summary>What a title's level was drawn from.</summary>
    /// <param name="State">The library state.</param>
    /// <param name="Entry">The title, or null while a scan or apply has taken it out of the list.</param>
    /// <param name="Details">The title's evidence, or null.</param>
    /// <param name="Rom">The ROM editor choices its emulator rows offer.</param>
    /// <param name="Tracked">Whether its shortcut is on the Storage level, which adds the Storage row.</param>
    private sealed record EntryShown(
        GameLibraryState State,
        GameLibraryEntry? Entry,
        GameLibraryDetails? Details,
        RomEmulatorState Rom,
        bool Tracked);

    /// <summary>What a title's staged-artwork level was drawn from.</summary>
    /// <param name="State">The library state.</param>
    /// <param name="Entry">The title.</param>
    /// <param name="Options">The slot's candidates, or null.</param>
    private sealed record ArtworkShown(GameLibraryState State, GameLibraryEntry Entry, GameLibraryOptionsAnswer? Options);

    /// <summary>What the ROM source editor was drawn from besides its draft.</summary>
    /// <param name="State">The library state.</param>
    /// <param name="Rom">The installed emulator choices.</param>
    /// <param name="Systems">The offered systems.</param>
    private sealed record RomEditorShown(
        GameLibraryState State,
        RomEmulatorState Rom,
        IReadOnlyList<(string Id, string Name)> Systems);
}
