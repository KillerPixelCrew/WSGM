using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Notes in Steam: the page that draws the shared Notes documents in Big Picture and in a game's Steam
///     overlay, over the same <see cref="NotesService" /> the WSGM overlay uses.
/// </summary>
/// <remarks>
///     <para>
///         Steam keeps no copy: every edit is a command carrying the page revision it was made against, and the
///         page draws only what this surface publishes. Nothing is put in the page's storage, so a reload of
///         Steam's frontend loses nothing and creates nothing.
///     </para>
///     <para>
///         Images are not part of the published state, so an edit never resends them; the page asks for each
///         image it shows with <c>image</c> and keeps the answer in memory while that page is open.
///     </para>
/// </remarks>
internal static class NotesSteamSurface
{
    /// <summary>The state and command namespace.</summary>
    internal const string PatchId = "wsgm.notes";

    /// <summary>The route the page is served at.</summary>
    internal const string Route = "/wsgm/notes";

    /// <summary>The renderer that draws it.</summary>
    internal const string Template = "notes";

    /// <summary>The name the page's gate registers under.</summary>
    internal const string GateName = "notes";

    /// <summary>Installs the page renderer and its state subscription.</summary>
    internal static ISteamUiPatch Patch { get; } = SteamPagePatch.Create(
        PatchId, GateName, "notes-v1:steam-page", "Notes");

    /// <summary>Declares the page's state and its exact command vocabulary.</summary>
    /// <param name="enabled">Whether state may be published: the host's pages on.</param>
    /// <param name="service">The shared Notes owner.</param>
    /// <returns>A module borrowing the service; construction does not install its patch.</returns>
    internal static ISteamUiModule Module(Func<bool> enabled, NotesService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return new SteamUiModule("notes", [Patch],
            [
                SteamUiModuleBuilder.Publication(PatchId, enabled,
                    () => new ValueTask<NotesSteamState?>(ReadState(service)),
                    NotesSteamJsonContext.Default.NotesSteamState, () => service.Revision)
            ],
            [
                Command("open", request => Open(service, request)),
                Command("report", request => Report(service, request)),
                Command("selectScope", request => SelectScope(service, request)),
                Command("openPage", request => OpenPage(service, request)),
                Command("closePage", _ => ClosePage(service)),
                Command("create", request => Create(service, request)),
                Command("rename", request => Edit(request, r => service.RenamePage(r.PageId!, r.Revision!.Value, r.Title ?? ""))),
                Command("addText", request => Edit(request, r => r is { X: { } x, Y: { } y }
                    ? service.AddText(r.PageId!, r.Revision!.Value, x, y, r.Text ?? "")
                    : NoteEditResult.Refuse(Invalid))),
                Command("setText", request => Edit(request, r => r.ElementId is { } id && r.Text is { } text
                    ? service.SetText(r.PageId!, r.Revision!.Value, id, text)
                    : NoteEditResult.Refuse(Invalid))),
                Command("place", request => Edit(request, r => r is { ElementId: { } id, X: { } x, Y: { } y, Width: { } w, Height: { } h }
                    ? service.PlaceElement(r.PageId!, r.Revision!.Value, id, x, y, w, h)
                    : NoteEditResult.Refuse(Invalid))),
                Command("raise", request => Edit(request, r => r.ElementId is { } id
                    ? service.RaiseElement(r.PageId!, r.Revision!.Value, id)
                    : NoteEditResult.Refuse(Invalid))),
                Command("remove", request => Edit(request, r => r.ElementId is { } id
                    ? service.RemoveElement(r.PageId!, r.Revision!.Value, id)
                    : NoteEditResult.Refuse(Invalid))),
                Command("addStroke", request => Edit(request, r => r.Stroke is { } stroke
                    ? service.AddStrokes(r.PageId!, r.Revision!.Value, [stroke])
                    : NoteEditResult.Refuse(Invalid))),
                Command("restoreStrokes", request => Edit(request, r => r.Strokes is { Count: > 0 } strokes
                    ? service.AddStrokes(r.PageId!, r.Revision!.Value, strokes, r.Indices)
                    : NoteEditResult.Refuse(Invalid))),
                Command("removeStrokes", request => Edit(request, r => r.StrokeIds is { Count: > 0 } ids
                    ? service.RemoveStrokes(r.PageId!, r.Revision!.Value, ids.ToHashSet(StringComparer.Ordinal))
                    : NoteEditResult.Refuse(Invalid))),
                Command("clearInk", request => Edit(request, r => service.RemoveStrokes(r.PageId!, r.Revision!.Value, null))),
                Command("deletePage", request => DeletePage(service, request)),
                Command("pin", request => request is { PageId: { } id, Pinned: { } pinned }
                    ? service.SetPinned(id, pinned).ToCommandResult()
                    : SteamUiCommandResult.Invalid(Invalid)),
                Command("move", request => request is { PageId: { } id, Delta: { } delta }
                    ? service.MovePage(id, delta).ToCommandResult()
                    : SteamUiCommandResult.Invalid(Invalid)),
                Command("capture", request => StartCapture(service, request)),
                Command("cancelCapture", _ => CancelCapture(service)),
                Command("image", request => Image(service, request)),
                Command("dismiss", _ => Dismiss(service))
            ]);
    }

    /// <summary>The page's state now.</summary>
    /// <param name="service">The shared owner.</param>
    /// <returns>A light unloaded state until the page has opened once; afterwards the full state.</returns>
    internal static NotesSteamState ReadState(NotesService service)
    {
        var view = service.SteamView.Read();
        var capture = service.Capture.Status;
        var captureState = new NotesSteamCapture(Camel(capture.Phase.ToString()), Camel(capture.Origin.ToString()),
            capture.PageId, capture.Remaining, capture.Outcome, capture.OutcomeIsError, capture.InsertedElementId);
        var game = view.AppId != 0 ? service.Games.ResolveSteamApp(view.AppId) : service.Games.ResolveCurrentGame();
        var steamGame = new NotesSteamGame(Camel(game.State.ToString()), game.Scope, game.Title, game.Detail);
        if (!view.Opened || !service.Loaded)
        {
            // Notes stays free until the page is first opened: nothing is read for a page nobody looks at.
            return new NotesSteamState(false, null, view.Notice, steamGame, NoteScope.Global, "Global", [], [], null,
                false, captureState, service.CountdownSeconds, view.ProcessId != 0);
        }

        var scope = view.Scope ?? game.Scope ?? NoteScope.Global;
        var page = view.PageId is { } pageId ? service.ReadPage(pageId) : null;
        return new NotesSteamState(true, service.LoadError, view.Notice, steamGame, scope, service.NotebookTitle(scope),
            service.ReadNotebooks(game), service.ReadPages(scope), page, page is not null && service.IsReadOnly(page.Id),
            captureState, service.CountdownSeconds, view.ProcessId != 0);
    }

    private const string Invalid = "The Notes request is malformed.";

    private static SteamUiCommandHandler Command(string name, Func<NotesSteamRequest, SteamUiCommandResult> apply)
    {
        return SteamUiModuleBuilder.Command<NotesSteamRequest>(PatchId, name, TryRead,
            (request, _) => Task.Run(() => apply(request)), Invalid);
    }

    /// <summary>Reads a request; unknown fields and wrong types are refused.</summary>
    /// <param name="payload">The payload.</param>
    /// <param name="request">The request.</param>
    /// <returns>Whether it has the request shape.</returns>
    internal static bool TryRead(JsonElement payload, out NotesSteamRequest request)
    {
        request = new NotesSteamRequest();
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            request = payload.Deserialize(NotesSteamJsonContext.Default.NotesSteamRequest) ?? new NotesSteamRequest();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static SteamUiCommandResult Open(NotesService service, NotesSteamRequest request)
    {
        if (!service.EnsureLoaded())
        {
            return new SteamUiCommandResult(false, service.LoadError);
        }

        var appId = request.AppId ?? 0;
        var processId = request.ProcessId ?? 0;
        if (service.SteamView.SetWindow(appId, processId))
        {
            // A new window starts on its game's notebook. In a game overlay that is the overlay's own app;
            // in Big Picture's main window it is whatever the session reports as running.
            var game = appId != 0 ? service.Games.ResolveSteamApp(appId) : service.Games.ResolveCurrentGame();
            var current = service.SteamView.Read();
            var keepPage = current.PageId is { } page && service.ReadPage(page) is { } open
                                                      && (game.Scope is null || open.Scope == game.Scope);
            service.SteamView.Select(game.Scope ?? NoteScope.Global, keepPage ? current.PageId : null);
            Log.Info($"Notes: Steam page opened (app {appId}, pid {processId}, notebook {game.Scope ?? "global"}).");
        }

        service.Publish();
        return SteamUiCommandResult.Applied;
    }

    private static SteamUiCommandResult Report(NotesService service, NotesSteamRequest request)
    {
        if (request.Capabilities is not { Length: > 0 and <= 4096 } report)
        {
            return SteamUiCommandResult.Invalid(Invalid);
        }

        if (service.SteamView.RecordCapabilities(report))
        {
            // The Steam client's browser capabilities, recorded so ink support is decided by evidence.
            Log.Info($"Notes: Steam page capabilities {report}");
        }

        return SteamUiCommandResult.Applied;
    }

    private static SteamUiCommandResult SelectScope(NotesService service, NotesSteamRequest request)
    {
        if (!NoteScope.IsValid(request.Scope))
        {
            return SteamUiCommandResult.Invalid(Invalid);
        }

        service.SteamView.Select(request.Scope, null);
        service.Publish();
        return SteamUiCommandResult.Applied;
    }

    private static SteamUiCommandResult OpenPage(NotesService service, NotesSteamRequest request)
    {
        if (request.PageId is not { } pageId || service.ReadPage(pageId) is not { } page)
        {
            return new SteamUiCommandResult(false, "That note no longer exists.");
        }

        service.SteamView.Select(page.Scope, page.Id);
        service.Publish();
        return SteamUiCommandResult.Applied;
    }

    private static SteamUiCommandResult ClosePage(NotesService service)
    {
        var view = service.SteamView.Read();
        service.SteamView.Select(view.Scope, null);
        service.Publish();
        return SteamUiCommandResult.Applied;
    }

    private static SteamUiCommandResult Create(NotesService service, NotesSteamRequest request)
    {
        if (!NoteScope.IsValid(request.Scope))
        {
            return SteamUiCommandResult.Invalid(Invalid);
        }

        var result = service.CreatePage(request.Scope!, request.Title);
        if (result is { Succeeded: true, Page: { } page })
        {
            service.SteamView.Select(page.Scope, page.Id);
            service.Publish();
        }

        return result.ToCommandResult();
    }

    private static SteamUiCommandResult Edit(NotesSteamRequest request, Func<NotesSteamRequest, NoteEditResult> edit)
    {
        if (!NoteDocumentRules.IsId(request.PageId) || request.Revision is not > 0)
        {
            return SteamUiCommandResult.Invalid(Invalid);
        }

        var result = edit(request);
        return result.Status == NoteEditStatus.Applied
            ? result.ElementId is { } id
                ? new SteamUiCommandResult(true, null, JsonSerializer.SerializeToElement(new NotesSteamCreated(id),
                    NotesSteamJsonContext.Default.NotesSteamCreated))
                : SteamUiCommandResult.Applied
            : Refusal(result);
    }

    /// <summary>The prefix a conflict refusal carries, so the page can offer reapplying.</summary>
    internal const string ConflictPrefix = "Conflict: ";

    private static SteamUiCommandResult Refusal(NoteEditResult result)
    {
        return new SteamUiCommandResult(false,
            result.Status == NoteEditStatus.Conflict ? ConflictPrefix + result.Error : result.Error);
    }

    private static SteamUiCommandResult DeletePage(NotesService service, NotesSteamRequest request)
    {
        if (!NoteDocumentRules.IsId(request.PageId) || request.Revision is not > 0)
        {
            return SteamUiCommandResult.Invalid(Invalid);
        }

        var result = service.DeletePage(request.PageId!, request.Revision.Value);
        if (!result.Succeeded)
        {
            return Refusal(result);
        }

        var view = service.SteamView.Read();
        service.SteamView.Select(view.Scope, null);
        service.Publish();
        return SteamUiCommandResult.Applied;
    }

    private static SteamUiCommandResult StartCapture(NotesService service, NotesSteamRequest request)
    {
        var view = service.SteamView.Read();
        if (!NoteDocumentRules.IsId(request.PageId) || request.Seconds is not { } seconds)
        {
            return SteamUiCommandResult.Invalid(Invalid);
        }

        if (view.ProcessId == 0 || view.AppId == 0)
        {
            return new SteamUiCommandResult(false,
                "Screenshots are taken from a game's Steam overlay. Open Notes over the game first.");
        }

        var game = service.Games.ResolveSteamApp(view.AppId);
        var target = new NotesCaptureTarget(view.ProcessId, 0, view.AppId, game.Title);
        return service.StartCapture(NotesCaptureOrigin.Steam, request.PageId!, seconds, target);
    }

    private static SteamUiCommandResult CancelCapture(NotesService service)
    {
        return service.Capture.Cancel("Screenshot cancelled.")
            ? SteamUiCommandResult.Applied
            : new SteamUiCommandResult(false, "No screenshot is pending.");
    }

    private static SteamUiCommandResult Image(NotesService service, NotesSteamRequest request)
    {
        if (!NoteAssets.IsAssetId(request.Asset))
        {
            return SteamUiCommandResult.Invalid(Invalid);
        }

        var bytes = service.ReadAsset(request.Asset!);
        if (bytes is null || ImageHeader.Format(bytes) is not ("png" or "jpg")
            || ImageHeader.DataUrl(bytes) is not { } url)
        {
            return new SteamUiCommandResult(false, "The image is missing.");
        }

        return new SteamUiCommandResult(true, null,
            JsonSerializer.SerializeToElement(new NotesSteamImage(request.Asset!, url),
                NotesSteamJsonContext.Default.NotesSteamImage));
    }

    private static SteamUiCommandResult Dismiss(NotesService service)
    {
        service.SteamView.SetNotice(null);
        service.Capture.ClearOutcome();
        service.Publish();
        return SteamUiCommandResult.Applied;
    }

    private static string Camel(string value)
    {
        return value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];
    }
}
