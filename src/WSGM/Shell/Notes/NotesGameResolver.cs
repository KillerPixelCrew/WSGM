using System;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Finds the notebook a game belongs to, from the session's running-application identity or a Steam AppID.</summary>
/// <remarks>
///     A game's notebook is keyed by its stable identity, never its title. A name learned on the way is remembered
///     as the notebook's title. When the running game cannot be resolved, the answer says why; nothing is ever
///     resolved to Global on the strength of a failed lookup.
/// </remarks>
internal sealed class NotesGameResolver
{
    private readonly Func<uint, (string Source, string Key, string Name)?>? _libraryTitle;
    private readonly NotesService _notes;
    private readonly Func<RunningApplicationTargetSnapshot>? _runningTarget;
    private readonly CancellationToken _shutdown;
    private readonly Func<uint, CancellationToken, Task<string?>>? _steamName;

    /// <summary>Creates the resolver for one Notes owner.</summary>
    /// <param name="notes">The owner whose notebook titles a resolution names and remembers.</param>
    /// <param name="runningTarget">The session's running-application identity; null where there is none.</param>
    /// <param name="libraryTitle">Finds the Game Library title a Steam shortcut id belongs to.</param>
    /// <param name="steamName">Reads a Steam app's name; null without Steam.</param>
    /// <param name="shutdown">The owner's shutdown; it cancels a pending Steam name read.</param>
    internal NotesGameResolver(NotesService notes, Func<RunningApplicationTargetSnapshot>? runningTarget,
        Func<uint, (string Source, string Key, string Name)?>? libraryTitle,
        Func<uint, CancellationToken, Task<string?>>? steamName, CancellationToken shutdown)
    {
        _notes = notes;
        _runningTarget = runningTarget;
        _libraryTitle = libraryTitle;
        _steamName = steamName;
        _shutdown = shutdown;
    }

    /// <summary>Resolves the running game to its notebook from the session's identity snapshot.</summary>
    /// <returns>The game, no game, or unknown with the reason.</returns>
    internal NotesCurrentGame ResolveCurrentGame()
    {
        if (_runningTarget is null)
        {
            return NotesCurrentGame.NoGame;
        }

        var snapshot = _runningTarget();
        switch (snapshot.State)
        {
            case RunningApplicationTargetState.Global:
                return NotesCurrentGame.NoGame;
            case RunningApplicationTargetState.Ambiguous:
                return Unknown("More than one game is running, so Notes cannot tell which one this is.");
            case RunningApplicationTargetState.Unavailable:
                return Unknown(snapshot.Diagnostic ?? "The running game is not known right now.");
        }

        if (snapshot.SteamAppId is { } appId)
        {
            return ResolveSteamApp(appId);
        }

        if (snapshot.ApplicationId is { } application
            && application.StartsWith(ProfileResolver.ProcessIdPrefix, StringComparison.Ordinal)
            && NoteScope.IsValid(application))
        {
            var title = snapshot.RtssProfileName ?? application[ProfileResolver.ProcessIdPrefix.Length..];
            _notes.RememberTitle(application, title);
            return new NotesCurrentGame(NotesGameState.Game, application, _notes.NotebookTitle(application));
        }

        return Unknown("The running game has no stable identity.");
    }

    /// <summary>Resolves a Steam AppID, a shortcut's included, to its notebook.</summary>
    /// <param name="appId">The app id.</param>
    /// <returns>The game; a WSGM-imported shortcut maps to its durable library identity.</returns>
    internal NotesCurrentGame ResolveSteamApp(uint appId)
    {
        string scope;
        if (SteamApps.IsShortcutAppId(appId) && _libraryTitle?.Invoke(appId) is { } library)
        {
            scope = NoteScope.ForLibraryTitle(library.Source, library.Key);
            _notes.RememberTitle(scope, library.Name);
        }
        else
        {
            scope = NoteScope.ForSteamApp(appId);
            QueueSteamName(scope, appId);
        }

        return new NotesCurrentGame(NotesGameState.Game, scope, _notes.NotebookTitle(scope));
    }

    private void QueueSteamName(string scope, uint appId)
    {
        if (_steamName is null || _notes.HasNotebookTitle(scope))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                _notes.RememberTitle(scope, await _steamName(appId, _shutdown).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Info($"Notes: the name of Steam app {appId} could not be read: {ex.Message}");
            }
        });
    }

    private static NotesCurrentGame Unknown(string detail)
    {
        return new NotesCurrentGame(NotesGameState.Unknown, null, "Current game unknown", detail);
    }
}
