namespace WSGM.PackagedLaunch;

/// <summary>Which input route the user asked for.</summary>
/// <remarks>
///     The launcher's own vocabulary rather than the shortcut's. The shared command line is compiled
///     into both this assembly and WSGM, so keeping it out of this project's public surface means the
///     test project sees exactly one copy of each shared type. <c>Program</c> maps between the two,
///     and that mapping stops compiling if a mode is ever added.
/// </remarks>
public enum RequestedInputMode
{
    /// <summary>Give the real game Steam's overlay and Steam Input.</summary>
    SteamOverlay,

    /// <summary>Switch the managed controller and inject nothing.</summary>
    ControllerOnly
}

/// <summary>What the activated process turned out to be.</summary>
public enum PackagedRuntime
{
    /// <summary>Neither shape could be established from the activated process.</summary>
    Unknown,

    /// <summary>A low-integrity AppContainer title, the Moonlighter shape.</summary>
    AppContainer,

    /// <summary>A full-trust packaged title with GDK evidence, the PowerWash shape.</summary>
    PackagedWin32
}

/// <summary>The route a session runs.</summary>
public enum LaunchRoute
{
    /// <summary>Supervise only: activate, contain, wait. Nothing is written into the game.</summary>
    SuperviseOnly,

    /// <summary>Switch the managed controller for the session, and inject nothing.</summary>
    ControllerOnly,

    /// <summary>Early Steam setup in the AAM-returned helper, then Steam's own child handoff.</summary>
    PackagedWin32Overlay,

    /// <summary>Early renderer injection, the desktop IPC broker and the input bridge.</summary>
    AppContainerOverlay
}

/// <summary>The chosen route and why, in one sentence fit for the log and a bug report.</summary>
/// <param name="Route">What to run.</param>
/// <param name="Reason">Why this route, naming the deciding evidence.</param>
/// <param name="Degraded">
///     Whether the session cannot do what the user asked, but is still worth running. A degraded
///     session reports a distinct exit code, because a game that ran without its overlay is not the
///     same outcome as one that ran with it.
/// </param>
public sealed record LaunchRouteDecision(LaunchRoute Route, string Reason, bool Degraded = false);

/// <summary>Chooses the route for one activated game.</summary>
/// <remarks>
///     The whole injection policy, as one pure function over what was asked for and what the
///     activated process turned out to be. Nothing else in this project decides whether to inject,
///     so the invariants are provable here: controller-only never injects, and neither does a
///     runtime this could not establish.
/// </remarks>
public static class LaunchRouteSelector
{
    /// <summary>Chooses the route.</summary>
    /// <param name="mode">Which input route the user asked for.</param>
    /// <param name="runtime">What the activated process turned out to be.</param>
    /// <returns>The route and the reason for it.</returns>
    public static LaunchRouteDecision Select(RequestedInputMode mode, PackagedRuntime runtime)
    {
        // Asked for first. Controller-only is a user decision about their own input, and no
        // property of the game can turn it into a route that writes into that game.
        if (mode is RequestedInputMode.ControllerOnly)
        {
            return new LaunchRouteDecision(
                LaunchRoute.ControllerOnly,
                "Controller-only was requested, so nothing is injected into the game.");
        }

        return runtime switch
        {
            PackagedRuntime.AppContainer => new LaunchRouteDecision(
                LaunchRoute.AppContainerOverlay,
                "The game runs in an AppContainer, which needs the desktop IPC broker and the input bridge."),
            PackagedRuntime.PackagedWin32 => new LaunchRouteDecision(
                LaunchRoute.PackagedWin32Overlay,
                "The game is a full-trust packaged title, so Steam is set up in its launch helper."),

            // No validated route exists for a title neither shape describes, and guessing one would
            // mean writing into a game on the strength of a guess. The session still runs: the user
            // asked to play, and Steam's running state is worth keeping either way.
            _ => new LaunchRouteDecision(
                LaunchRoute.SuperviseOnly,
                "The game is neither an AppContainer nor a packaged Win32 title, so no overlay route "
                + "applies and nothing is injected.",
                true)
        };
    }
}
