using System;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>How the boot movies reach Steam's own startup movie choice.</summary>
/// <param name="SetAside">Puts Steam on its default movie and answers what it held.</param>
/// <param name="Restore">Gives Steam back a choice set aside, unless the user has chosen anew since.</param>
/// <param name="WhenReady">
///     Runs one attempt once the Steam UI transport opens and again at each later ready edge while it
///     answers false, as <see cref="SteamUiReadiness.RunWhenReadyAsync" /> does.
/// </param>
internal sealed record SteamStartupMovieAccess(
    Func<CancellationToken, Task<SteamStartupMovieResult>> SetAside,
    Func<SteamStartupMovieChoice, CancellationToken, Task<SteamStartupMovieResult>> Restore,
    Func<string, Func<CancellationToken, Task<bool>>, CancellationToken, Task<bool>> WhenReady);
