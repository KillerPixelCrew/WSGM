using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The session's one Notes owner, shared by the WSGM overlay and Steam's overlay: the store, every edit with
///     its revision check, the notebook titles, both surfaces' places and the screenshot transaction.
/// </summary>
/// <remarks>
///     <para>
///         Nothing is read until Notes is first used, and nothing runs in the background: no timer, no polling.
///         Changes publish through <see cref="Changed" />, which both surfaces follow.
///     </para>
///     <para>
///         Every content edit names the page revision it was made against. A page that moved on refuses the
///         edit and hands back its current content, so the surface can offer reload or reapply instead of
///         silently overwriting the other overlay's work. Image insertion from a screenshot applies to the
///         current revision: it only adds an element.
///     </para>
///     <para>
///         A game's notebook is keyed by its stable identity, never its title; <see cref="Games" /> resolves
///         it. When the running game cannot be resolved, the surfaces say so and show Global explicitly;
///         nothing is written to Global on the strength of a failed resolution.
///     </para>
/// </remarks>
internal sealed class NotesService : IChangeSource, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, NotePage> _pages = new(StringComparer.Ordinal);
    private readonly HashSet<string> _readOnly = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly NoteStore _store;
    private readonly TimeProvider _time;
    private Task _captureRun = Task.CompletedTask;
    private bool _disposed;
    private string? _loadError;
    private bool _loaded;
    private NotesOverlayLocation _overlayLocation = NotesOverlayLocation.Start;
    private NotesTextDraft? _overlayTextDraft;
    private long _revision;
    private NoteSettings _settings = new();

    /// <summary>Creates the owner; nothing is read until Notes is first used.</summary>
    /// <param name="store">The files.</param>
    /// <param name="runningTarget">The session's running-application identity; null where there is none.</param>
    /// <param name="libraryTitle">Finds the Game Library title a Steam shortcut id belongs to.</param>
    /// <param name="steamName">Reads a Steam app's name; null without Steam.</param>
    /// <param name="time">The clock; replaced by tests.</param>
    internal NotesService(NoteStore store, Func<RunningApplicationTargetSnapshot>? runningTarget,
        Func<uint, (string Source, string Key, string Name)?>? libraryTitle,
        Func<uint, CancellationToken, Task<string?>>? steamName, TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = time ?? TimeProvider.System;
        Capture = new NotesCaptureTransaction(InsertCapturedImage, Publish);
        Games = new NotesGameResolver(this, runningTarget, libraryTitle, steamName, _shutdown.Token);
    }

    /// <summary>Increases whenever anything either surface shows changed.</summary>
    internal long Revision => Interlocked.Read(ref _revision);

    /// <summary>Finds the notebook of the running game or of a Steam app.</summary>
    internal NotesGameResolver Games { get; }

    /// <summary>The screenshot transaction.</summary>
    internal NotesCaptureTransaction Capture { get; }

    /// <summary>The effects a screenshot from the WSGM overlay uses; set by the overlay controller.</summary>
    internal NotesCapturePorts? OverlayCapturePorts { get; set; }

    /// <summary>The effects a screenshot from Steam's overlay uses; set by the Steam composition.</summary>
    internal NotesCapturePorts? SteamCapturePorts { get; set; }

    /// <summary>
    ///     Reports whether Steam's overlay is open over a game process, so a capture never records it; null
    ///     without Steam. Set by the Steam composition and used by both origins.
    /// </summary>
    internal Func<uint, CancellationToken, Task<bool>>? SteamOverlayOpen { get; set; }

    /// <summary>The one countdown display both origins show over the game.</summary>
    internal INotesCountdownDisplay? Countdown { get; set; }

    /// <summary>Whether the store has been read.</summary>
    internal bool Loaded
    {
        get
        {
            lock (_gate)
            {
                return _loaded;
            }
        }
    }

    /// <summary>Why the store could not be read, or null.</summary>
    internal string? LoadError
    {
        get
        {
            lock (_gate)
            {
                return _loadError;
            }
        }
    }

    /// <summary>The countdown the next screenshot offers first.</summary>
    internal int CountdownSeconds
    {
        get
        {
            lock (_gate)
            {
                return _settings.CaptureCountdownSeconds;
            }
        }
    }

    /// <summary>Where the overlay's Notes was.</summary>
    internal NotesOverlayLocation OverlayLocation
    {
        get
        {
            lock (_gate)
            {
                return _overlayLocation;
            }
        }
        set
        {
            lock (_gate)
            {
                _overlayLocation = value ?? NotesOverlayLocation.Start;
            }
        }
    }

    /// <summary>Unsaved overlay text, kept across an overlay close.</summary>
    internal NotesTextDraft? OverlayTextDraft
    {
        get
        {
            lock (_gate)
            {
                return _overlayTextDraft;
            }
        }
        set
        {
            lock (_gate)
            {
                _overlayTextDraft = value;
            }
        }
    }

    /// <summary>The Steam surface's state; see <see cref="NotesSteamSurface" />.</summary>
    internal NotesSteamView SteamView { get; } = new();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // A pending screenshot ends without reopening anything; the page it targeted is already saved.
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await _captureRun.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Notes: the screenshot transaction ended with an error at shutdown: {ex.Message}");
        }

        _shutdown.Dispose();
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>Reads the store the first time Notes is used; later calls return at once.</summary>
    /// <returns>Whether the store is readable.</returns>
    /// <remarks>Reads files: call it off the UI thread.</remarks>
    internal bool EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return _loadError is null;
            }

            try
            {
                var snapshot = _store.Load();
                foreach (var page in snapshot.Pages)
                {
                    _pages[page.Id] = page;
                }

                _readOnly.UnionWith(snapshot.ReadOnlyPages);
                // Names learned before the first load (the running game resolved first) are kept.
                foreach (var (scope, title) in _settings.NotebookTitles)
                {
                    snapshot.Settings.NotebookTitles.TryAdd(scope, title);
                }

                _settings = snapshot.Settings;
                _loadError = null;
                Log.Info($"Notes: loaded {_pages.Count} pages"
                         + (snapshot.SetAside.Count > 0 ? $", set {snapshot.SetAside.Count} unreadable files aside." : "."));
            }
            catch (NoteStoreException ex)
            {
                _loadError = ex.Message;
                Log.Warn($"Notes: the store could not be read: {ex.InnerException?.Message ?? ex.Message}");
            }

            _loaded = true;
        }

        Publish();
        return LoadError is null;
    }

    /// <summary>Every notebook with pages, plus Global and the running game's, Global first.</summary>
    /// <param name="current">The running game, whose notebook is listed even while empty.</param>
    /// <returns>The notebooks.</returns>
    internal IReadOnlyList<NotebookSummary> ReadNotebooks(NotesCurrentGame? current = null)
    {
        lock (_gate)
        {
            var counts = _pages.Values.GroupBy(page => page.Scope, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            if (current is { State: NotesGameState.Game, Scope: { } scope })
            {
                counts.TryAdd(scope, 0);
            }

            counts.TryAdd(NoteScope.Global, 0);
            return
            [
                .. counts.Select(pair => new NotebookSummary(pair.Key, TitleLocked(pair.Key), pair.Value))
                    .OrderBy(book => book.Scope == NoteScope.Global ? 0 : 1)
                    .ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase)
            ];
        }
    }

    /// <summary>A notebook's pages, pinned first, then in their order.</summary>
    /// <param name="scope">The notebook.</param>
    /// <returns>The summaries.</returns>
    internal IReadOnlyList<NotePageSummary> ReadPages(string scope)
    {
        lock (_gate)
        {
            return
            [
                .. _pages.Values.Where(page => page.Scope == scope)
                    .OrderByDescending(page => page.Pinned).ThenBy(page => page.Order)
                    .ThenByDescending(page => page.Modified).Select(Summarize)
            ];
        }
    }

    /// <summary>A detached copy of one page.</summary>
    /// <param name="id">The page id.</param>
    /// <returns>The copy, or null when it does not exist.</returns>
    internal NotePage? ReadPage(string id)
    {
        lock (_gate)
        {
            return _pages.TryGetValue(id, out var page) ? page.Clone() : null;
        }
    }

    /// <summary>Whether a page was written by a newer WSGM and must not be changed.</summary>
    /// <param name="id">The page id.</param>
    /// <returns>True for a read-only page.</returns>
    internal bool IsReadOnly(string id)
    {
        lock (_gate)
        {
            return _readOnly.Contains(id);
        }
    }

    /// <summary>A notebook's display name.</summary>
    /// <param name="scope">The notebook.</param>
    /// <returns>"Global", the game's last known name, or its identity.</returns>
    internal string NotebookTitle(string scope)
    {
        lock (_gate)
        {
            return TitleLocked(scope);
        }
    }

    /// <summary>Whether a notebook has a remembered title.</summary>
    /// <param name="scope">The notebook.</param>
    /// <returns>True once a name was learned for it.</returns>
    internal bool HasNotebookTitle(string scope)
    {
        lock (_gate)
        {
            return _settings.NotebookTitles.ContainsKey(scope);
        }
    }

    /// <summary>Remembers a game's name as its notebook's title, saved with the settings once Notes is loaded.</summary>
    /// <param name="scope">The notebook.</param>
    /// <param name="title">The name; nothing changes for a blank one.</param>
    internal void RememberTitle(string scope, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        lock (_gate)
        {
            if (_settings.NotebookTitles.TryGetValue(scope, out var known) && known == title)
            {
                return;
            }

            _settings.NotebookTitles[scope] = title.Trim();
            if (_loaded && _loadError is null)
            {
                TryWriteSettingsLocked();
            }
        }

        Publish();
    }

    /// <summary>Creates an empty page at the top of a notebook.</summary>
    /// <param name="scope">The notebook.</param>
    /// <param name="title">The title, or null for "Untitled page".</param>
    /// <returns>The created page.</returns>
    internal NoteEditResult CreatePage(string scope, string? title = null)
    {
        if (!NoteScope.IsValid(scope))
        {
            return NoteEditResult.Refuse("That notebook is not a recognized game or Global.");
        }

        NoteEditResult result;
        lock (_gate)
        {
            if (Refusal() is { } refusal)
            {
                return refusal;
            }

            var now = _time.GetUtcNow();
            var siblings = _pages.Values.Where(page => page.Scope == scope).ToArray();
            NotePage page = new()
            {
                Id = NoteDocumentRules.NewId(), Scope = scope,
                Title = string.IsNullOrWhiteSpace(title) ? "Untitled page" : title.Trim(),
                Order = siblings.Length == 0 ? 0 : siblings.Min(item => item.Order) - 1,
                Created = now, Modified = now, Revision = 1
            };
            result = WriteLocked(page, null);
        }

        Publish();
        return result;
    }

    /// <summary>Renames a page.</summary>
    internal NoteEditResult RenamePage(string pageId, long expectedRevision, string title)
    {
        return Edit(pageId, expectedRevision, page =>
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return "A page needs a title.";
            }

            page.Title = title.Trim();
            return null;
        });
    }

    /// <summary>Adds a text block.</summary>
    /// <param name="pageId">The page.</param>
    /// <param name="expectedRevision">The revision the editor shows.</param>
    /// <param name="x">Left edge in page units.</param>
    /// <param name="y">Top edge in page units.</param>
    /// <param name="text">The text; may be empty.</param>
    /// <returns>The result; <see cref="NoteEditResult.ElementId" /> names the block.</returns>
    internal NoteEditResult AddText(string pageId, long expectedRevision, double x, double y, string text)
    {
        var id = NoteDocumentRules.NewId();
        return Edit(pageId, expectedRevision, page =>
        {
            page.Elements.Add(new NoteElement
            {
                Id = id, Kind = NoteElement.TextKind, X = Clamp(x), Y = Math.Max(0, y), Width = 560, Height = 120,
                Text = text ?? ""
            });
            return null;
        }, id);
    }

    /// <summary>Replaces a text block's text.</summary>
    internal NoteEditResult SetText(string pageId, long expectedRevision, string elementId, string text)
    {
        return Edit(pageId, expectedRevision, page =>
        {
            if (page.Elements.FirstOrDefault(item => item.Id == elementId) is not { Kind: NoteElement.TextKind } element)
            {
                return Missing;
            }

            element.Text = text ?? "";
            return null;
        });
    }

    /// <summary>Moves and resizes an element.</summary>
    internal NoteEditResult PlaceElement(string pageId, long expectedRevision, string elementId,
        double x, double y, double width, double height)
    {
        return Edit(pageId, expectedRevision, page =>
        {
            if (page.Elements.FirstOrDefault(item => item.Id == elementId) is not { } element)
            {
                return Missing;
            }

            if (!double.IsFinite(width) || !double.IsFinite(height) || width < 24 || height < 24)
            {
                return "That size is too small to use.";
            }

            element.X = Clamp(x);
            element.Y = Math.Max(0, y);
            element.Width = width;
            element.Height = height;
            return null;
        });
    }

    /// <summary>Brings an element in front of the others.</summary>
    internal NoteEditResult RaiseElement(string pageId, long expectedRevision, string elementId)
    {
        return Edit(pageId, expectedRevision, page =>
        {
            var index = page.Elements.FindIndex(item => item.Id == elementId);
            if (index < 0)
            {
                return Missing;
            }

            var element = page.Elements[index];
            page.Elements.RemoveAt(index);
            page.Elements.Add(element);
            return null;
        });
    }

    /// <summary>Removes a text block or image; the page and its other content stay.</summary>
    internal NoteEditResult RemoveElement(string pageId, long expectedRevision, string elementId)
    {
        return Edit(pageId, expectedRevision,
            page => page.Elements.RemoveAll(item => item.Id == elementId) == 0 ? Missing : null);
    }

    /// <summary>Adds an ink stroke, or puts erased strokes back where they were.</summary>
    /// <param name="pageId">The page.</param>
    /// <param name="expectedRevision">The revision the stroke was drawn on.</param>
    /// <param name="strokes">The strokes; ids must be new to the page.</param>
    /// <param name="indices">Where each goes in drawing order, or null to append. Used by undo of an erase.</param>
    /// <returns>The result.</returns>
    internal NoteEditResult AddStrokes(string pageId, long expectedRevision, IReadOnlyList<NoteStroke> strokes,
        IReadOnlyList<int>? indices = null)
    {
        if (strokes.Count == 0 || (indices is not null && indices.Count != strokes.Count))
        {
            return NoteEditResult.Refuse("The ink is malformed.");
        }

        foreach (var stroke in strokes)
        {
            if (!NoteDocumentRules.TryValidate(stroke, out var error))
            {
                return NoteEditResult.Refuse(error);
            }
        }

        return Edit(pageId, expectedRevision, page =>
        {
            for (var index = 0; index < strokes.Count; index++)
            {
                var copy = Copy(strokes[index]);
                var at = indices is null ? page.Strokes.Count : Math.Clamp(indices[index], 0, page.Strokes.Count);
                page.Strokes.Insert(at, copy);
            }

            return null;
        }, strokes[0].Id);
    }

    /// <summary>Removes ink strokes: the eraser, undo of a stroke, or Clear ink.</summary>
    /// <param name="pageId">The page.</param>
    /// <param name="expectedRevision">The revision shown.</param>
    /// <param name="strokeIds">The strokes, or null for every stroke on the page.</param>
    /// <returns>The result.</returns>
    internal NoteEditResult RemoveStrokes(string pageId, long expectedRevision, IReadOnlyCollection<string>? strokeIds)
    {
        return Edit(pageId, expectedRevision, page =>
        {
            if (strokeIds is null)
            {
                page.Strokes.Clear();
                return null;
            }

            return page.Strokes.RemoveAll(stroke => strokeIds.Contains(stroke.Id)) == 0 ? Missing : null;
        });
    }

    /// <summary>Deletes a page.</summary>
    internal NoteEditResult DeletePage(string pageId, long expectedRevision)
    {
        NoteEditResult result;
        lock (_gate)
        {
            if (Refusal() is { } refusal)
            {
                return refusal;
            }

            if (!_pages.TryGetValue(pageId, out var page))
            {
                return new NoteEditResult(NoteEditStatus.Missing, null, "That note no longer exists.");
            }

            if (page.Revision != expectedRevision)
            {
                return new NoteEditResult(NoteEditStatus.Conflict, page.Clone(), ConflictMessage);
            }

            try
            {
                _store.DeletePage(pageId);
            }
            catch (NoteStoreException ex)
            {
                Log.Warn($"Notes: deleting page {pageId} failed: {ex.InnerException?.Message}");
                return NoteEditResult.Refuse(ex.Message);
            }

            _pages.Remove(pageId);
            _readOnly.Remove(pageId);
            if (_overlayTextDraft?.PageId == pageId)
            {
                _overlayTextDraft = null;
            }

            result = new NoteEditResult(NoteEditStatus.Applied, null, null);
            Log.Info($"Notes: deleted page {pageId}.");
        }

        Publish();
        return result;
    }

    /// <summary>Pins or unpins a page. Organization only: open drafts stay valid.</summary>
    internal NoteEditResult SetPinned(string pageId, bool pinned)
    {
        return Organize(pageId, page => page.Pinned = pinned);
    }

    /// <summary>Moves a page one place up or down among its notebook's pages of the same pin state.</summary>
    /// <param name="pageId">The page.</param>
    /// <param name="delta">-1 for up, +1 for down.</param>
    /// <returns>The result; moving past either end is a no-op success.</returns>
    internal NoteEditResult MovePage(string pageId, int delta)
    {
        NoteEditResult result;
        lock (_gate)
        {
            if (Refusal() is { } refusal)
            {
                return refusal;
            }

            if (!_pages.TryGetValue(pageId, out var page))
            {
                return new NoteEditResult(NoteEditStatus.Missing, null, "That note no longer exists.");
            }

            var siblings = _pages.Values.Where(item => item.Scope == page.Scope && item.Pinned == page.Pinned)
                .OrderBy(item => item.Order).ThenByDescending(item => item.Modified).ToList();
            var index = siblings.IndexOf(page);
            var target = index + Math.Sign(delta);
            if (target < 0 || target >= siblings.Count)
            {
                return new NoteEditResult(NoteEditStatus.Applied, page.Clone(), null);
            }

            siblings.RemoveAt(index);
            siblings.Insert(target, page);
            List<NotePage> changed = [];
            for (var position = 0; position < siblings.Count; position++)
            {
                if (siblings[position].Order != position)
                {
                    var copy = siblings[position].Clone();
                    copy.Order = position;
                    changed.Add(copy);
                }
            }

            foreach (var copy in changed)
            {
                if (_readOnly.Contains(copy.Id))
                {
                    continue;
                }

                var written = WriteLocked(copy, null);
                if (!written.Succeeded)
                {
                    return written;
                }
            }

            result = new NoteEditResult(NoteEditStatus.Applied, _pages[pageId].Clone(), null);
        }

        Publish();
        return result;
    }

    /// <summary>Remembers the countdown the player chose.</summary>
    /// <param name="seconds">5 to 10.</param>
    internal void SetCountdown(int seconds)
    {
        bool changed;
        lock (_gate)
        {
            changed = SetCountdownLocked(seconds);
        }

        if (changed)
        {
            Publish();
        }
    }

    /// <summary>An image asset's bytes.</summary>
    /// <param name="asset">The asset id.</param>
    /// <returns>The bytes, or null when missing.</returns>
    internal byte[]? ReadAsset(string asset)
    {
        return _store.ReadAsset(asset);
    }

    /// <summary>An image asset's file.</summary>
    /// <param name="asset">The asset id.</param>
    /// <returns>The path, or null for a malformed id.</returns>
    internal string? AssetPath(string asset)
    {
        return NoteAssets.IsAssetId(asset) ? _store.AssetPath(asset) : null;
    }

    /// <summary>Starts an Add Screenshot for a page and returns once it has ended.</summary>
    /// <param name="origin">The surface it starts from and returns to.</param>
    /// <param name="pageId">The page.</param>
    /// <param name="seconds">The countdown.</param>
    /// <param name="target">The game.</param>
    /// <returns>A refusal when it cannot start; otherwise applied once the transaction is running.</returns>
    internal SteamUiCommandResult StartCapture(NotesCaptureOrigin origin, string pageId, int seconds,
        NotesCaptureTarget target)
    {
        var ports = origin == NotesCaptureOrigin.Overlay ? OverlayCapturePorts : SteamCapturePorts;
        if (ports is null)
        {
            return new SteamUiCommandResult(false, "Screenshots are not available in this session.");
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return SteamUiCommandResult.Refused;
            }

            if (!_pages.ContainsKey(pageId))
            {
                return new SteamUiCommandResult(false, "That note no longer exists.");
            }

            if (_readOnly.Contains(pageId))
            {
                return new SteamUiCommandResult(false, ReadOnlyMessage);
            }

            if (Capture.Pending)
            {
                return new SteamUiCommandResult(false, "A screenshot is already pending.");
            }

            SetCountdownLocked(seconds);
        }

        // Started outside the lock: the transaction reports its first phase synchronously, and a surface
        // reading the state from that report must not find the lock held. It refuses a second run itself.
        var run = Capture.RunAsync(origin, pageId, seconds, target, ports, _shutdown.Token);
        lock (_gate)
        {
            _captureRun = run;
        }

        Log.Observe(run, "Notes screenshot");
        return SteamUiCommandResult.Applied;
    }

    /// <summary>Raises <see cref="Changed" /> after a change either surface shows.</summary>
    internal void Publish()
    {
        Interlocked.Increment(ref _revision);
        Changed?.Invoke();
    }

    private const string ConflictMessage =
        "This note changed in the other overlay since you opened it. It now shows the newest version.";

    private const string ReadOnlyMessage = "This note was saved by a newer WSGM and is read-only here.";
    private const string Missing = "\u0000missing";

    private NoteEditResult InsertCapturedImage(string pageId, byte[] image, int width, int height)
    {
        string asset;
        try
        {
            asset = _store.AddAsset(image);
        }
        catch (NoteStoreException ex)
        {
            Log.Warn($"Notes: storing a screenshot failed: {ex.InnerException?.Message ?? ex.Message}");
            return NoteEditResult.Refuse(ex.Message);
        }

        NoteEditResult result;
        var id = NoteDocumentRules.NewId();
        lock (_gate)
        {
            if (!_pages.TryGetValue(pageId, out var current))
            {
                return new NoteEditResult(NoteEditStatus.Missing, null,
                    "The note was deleted while the screenshot was taken.");
            }

            // Placed below the existing content at half the page width, aspect preserved, so it never covers
            // what the player was looking at.
            var displayWidth = NotePage.PageWidth / 2;
            var displayHeight = displayWidth * height / Math.Max(1, width);
            var top = current.Elements.Count == 0 && current.Strokes.Count == 0
                ? 24
                : NoteDocumentRules.ContentHeight(current) - 216;
            result = EditLocked(pageId, current.Revision, page =>
            {
                page.Elements.Add(new NoteElement
                {
                    Id = id, Kind = NoteElement.ImageKind, X = 24, Y = Math.Max(24, top), Width = displayWidth,
                    Height = displayHeight, Asset = asset, PixelWidth = width, PixelHeight = height
                });
                return null;
            }, id);
            if (result.Succeeded)
            {
                _overlayLocation = _overlayLocation with { PageId = pageId };
            }
        }

        Publish();
        return result;
    }

    private NoteEditResult Edit(string pageId, long expectedRevision, Func<NotePage, string?> change,
        string? createdId = null)
    {
        return Publishing(() => EditLocked(pageId, expectedRevision, change, createdId));
    }

    /// <summary>Runs one locked edit and publishes it when it was saved.</summary>
    private NoteEditResult Publishing(Func<NoteEditResult> locked)
    {
        NoteEditResult result;
        lock (_gate)
        {
            result = locked();
        }

        if (result.Succeeded)
        {
            Publish();
        }

        return result;
    }

    /// <summary>Finds a page this session may change.</summary>
    /// <returns>False with the refusal when Notes cannot write, the page is gone or it is read-only.</returns>
    private bool WritableLocked(string pageId, [NotNullWhen(true)] out NotePage? current,
        [NotNullWhen(false)] out NoteEditResult? refused)
    {
        current = null;
        refused = Refusal();
        if (refused is not null)
        {
            return false;
        }

        if (!_pages.TryGetValue(pageId, out current))
        {
            refused = new NoteEditResult(NoteEditStatus.Missing, null, "That note no longer exists.");
            return false;
        }

        if (_readOnly.Contains(pageId))
        {
            refused = NoteEditResult.Refuse(ReadOnlyMessage);
            return false;
        }

        return true;
    }

    private NoteEditResult EditLocked(string pageId, long expectedRevision, Func<NotePage, string?> change,
        string? createdId)
    {
        if (!WritableLocked(pageId, out var current, out var refused))
        {
            return refused;
        }

        if (current.Revision != expectedRevision)
        {
            Log.Info($"Notes: page {pageId} edit refused, revision {expectedRevision} is behind {current.Revision}.");
            return new NoteEditResult(NoteEditStatus.Conflict, current.Clone(), ConflictMessage);
        }

        var draft = current.Clone();
        var error = change(draft);
        if (error == Missing)
        {
            return new NoteEditResult(NoteEditStatus.Missing, current.Clone(),
                "That part of the note no longer exists.");
        }

        if (error is not null)
        {
            return NoteEditResult.Refuse(error);
        }

        draft.Revision = current.Revision + 1;
        draft.Modified = _time.GetUtcNow();
        if (!NoteDocumentRules.TryValidate(draft, out var invalid))
        {
            return NoteEditResult.Refuse(invalid);
        }

        return WriteLocked(draft, createdId);
    }

    private NoteEditResult Organize(string pageId, Action<NotePage> change)
    {
        return Publishing(() =>
        {
            if (!WritableLocked(pageId, out var current, out var refused))
            {
                return refused;
            }

            var copy = current.Clone();
            change(copy);
            return WriteLocked(copy, null);
        });
    }

    private NoteEditResult WriteLocked(NotePage page, string? createdId)
    {
        try
        {
            _store.WritePage(page);
        }
        catch (NoteStoreException ex)
        {
            Log.Warn($"Notes: saving page {page.Id} failed: {ex.InnerException?.Message ?? ex.Message}");
            return NoteEditResult.Refuse(ex.Message);
        }

        _pages[page.Id] = page;
        return new NoteEditResult(NoteEditStatus.Applied, page.Clone(), null, createdId);
    }

    private NoteEditResult? Refusal()
    {
        if (_disposed)
        {
            return NoteEditResult.Refuse("Notes is shutting down.");
        }

        if (!_loaded)
        {
            return NoteEditResult.Refuse("Notes has not finished loading.");
        }

        return _loadError is null ? null : NoteEditResult.Refuse(_loadError);
    }

    private string TitleLocked(string scope)
    {
        if (scope == NoteScope.Global)
        {
            return "Global";
        }

        if (_settings.NotebookTitles.TryGetValue(scope, out var title) && title.Length > 0)
        {
            return title;
        }

        return scope.StartsWith("steam:", StringComparison.Ordinal)
            ? "Steam app " + scope["steam:".Length..]
            : scope.StartsWith("process:", StringComparison.Ordinal)
                ? scope["process:".Length..]
                : "Imported game";
    }

    private bool SetCountdownLocked(int seconds)
    {
        seconds = NoteSettings.ClampCountdown(seconds);
        if (_settings.CaptureCountdownSeconds == seconds)
        {
            return false;
        }

        _settings.CaptureCountdownSeconds = seconds;
        TryWriteSettingsLocked();
        return true;
    }

    private void TryWriteSettingsLocked()
    {
        try
        {
            _store.WriteSettings(_settings);
        }
        catch (NoteStoreException ex)
        {
            // A preference or a cached name; the notes themselves are unaffected.
            Log.Warn($"Notes: settings were not saved: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private NotePageSummary Summarize(NotePage page)
    {
        var first = page.Elements.FirstOrDefault(element => element.Kind == NoteElement.TextKind
                                                            && !string.IsNullOrWhiteSpace(element.Text))?.Text ?? "";
        var preview = first.Trim();
        return new NotePageSummary(page.Id, page.Title, page.Pinned, page.Modified, page.Revision,
            _readOnly.Contains(page.Id), preview.Length > 240 ? preview[..240] + "…" : preview,
            page.Elements.Count(element => element.Kind == NoteElement.TextKind),
            page.Elements.Count(element => element.Kind == NoteElement.ImageKind), page.Strokes.Count);
    }

    private static double Clamp(double x)
    {
        return Math.Clamp(x, 0, NotePage.PageWidth - 24);
    }

    private static NoteStroke Copy(NoteStroke stroke)
    {
        return new NoteStroke
        {
            Id = stroke.Id, Tool = stroke.Tool, Color = stroke.Color, Width = stroke.Width,
            Points = [.. stroke.Points], Pressure = [.. stroke.Pressure]
        };
    }
}
