using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Shell;

/// <summary>The session's half of the live mode transitions, supplied once when the coordinator is built.</summary>
/// <param name="Entry">
///     The displays, audio, plugin actions, splash and desktop recovery record the entry and the
///     desktop return work through.
/// </param>
/// <param name="PrepareSteamUiForBigPictureAsync">
///     Awaited (bounded) immediately before a transition asks Steam for Big Picture, so the owner can
///     retract injected Steam UI state and close its transport first: the request rebuilds Steam's
///     whole front-end, and that rebuild must see stock client state. The token is the budget; the
///     owner closes its transport when it fires, whatever the retraction is still doing.
/// </param>
/// <param name="PrepareSteamUiForDesktopAsync">
///     Awaited (bounded) immediately before the desktop return asks Steam to close Big Picture, for
///     the same reason: closing rebuilds Steam's front-end just as opening does. The token is the
///     budget, as above.
/// </param>
/// <param name="SteamUiBigPictureRequestSettled">
///     Invoked when a transition that may have requested Big Picture has settled, on every outcome,
///     so the owner can lift the hold above. Idempotent.
/// </param>
/// <param name="GameModeEntrySettled">
///     Invoked once an entry attempt has settled, on every outcome, so the owner can dismiss the
///     splash. Idempotent.
/// </param>
/// <param name="DesktopReady">Invoked on the UI thread once Explorer is restored.</param>
/// <param name="IsGameMode">Whether the session is in Game Mode.</param>
/// <param name="ShutdownCancellation">Cancels an in-flight desktop restoration when the session stops.</param>
internal sealed record SessionModeHooks(
    IGameModeEntryBackend Entry,
    Func<CancellationToken, Task> PrepareSteamUiForBigPictureAsync,
    Func<CancellationToken, Task> PrepareSteamUiForDesktopAsync,
    Action SteamUiBigPictureRequestSettled,
    Action GameModeEntrySettled,
    Action DesktopReady,
    Func<bool> IsGameMode,
    CancellationToken ShutdownCancellation = default);
