using System;
using System.Globalization;
using System.IO;

namespace WSGM.PackagedLaunch;

/// <summary>
///     The overlay route for a native UWP title: bridge Steam's IPC objects into the AppContainer,
///     route the engine's gamepad activation through Steam, and keep the foreground on the game's
///     own window.
/// </summary>
/// <remarks>
///     <para>
///         Each piece exists because removing it broke something in the recorded trial, and the
///         order they are listed in is the order they had to be fixed:
///     </para>
///     <list type="number">
///         <item>
///             Loading Steam's renderer alone registered the game with Steam — the process appeared
///             in Steam's own log and <c>gameoverlayui64</c> started for it — and produced no
///             working overlay and no input. Registration is not a functional pass.
///         </item>
///         <item>
///             Inside an AppContainer, Steam's object names resolve to a private namespace, so the
///             game and Steam created different objects with the same names. The broker fixes that.
///             Getting <c>SteamWebHelper_GPUProcRenderEvent</c> exactly right is what removed
///             "Failed creating CEF paint event: 5" and let the overlay survive Alt-Tab.
///         </item>
///         <item>
///             The overlay then worked and the controller still did not. Unity asks for
///             <c>IActivationFactory</c> and then queries the statics from it; Steam wraps the
///             direct statics request but not that second step, so the game got Windows' own
///             factory and saw zero pads. The input bridge routes only that path.
///         </item>
///         <item>
///             Input then worked until the first Alt-Tab, because the frame window belongs to
///             ApplicationFrameHost and Steam Input follows the foreground's owner. Raising the
///             game-owned CoreWindow restored it with nothing reinjected.
///         </item>
///     </list>
///     <para>
///         The bridge is a WSGM-built DLL with game-side hooks, which is materially more exposure to
///         an anti-cheat than the packaged Win32 route's use of Valve's own signed binaries. Nothing
///         here establishes that any anti-cheat accepts it.
///     </para>
/// </remarks>
internal sealed class AppContainerOverlayRoute(GameInjector injector) : IDisposable
{
    private OverlayObjectBroker? _broker;

    /// <summary>The bridge, beside this executable.</summary>
    private static string BridgePath =>
        Path.Combine(AppContext.BaseDirectory, "WsgmUwpBridge.dll");

    /// <inheritdoc />
    public void Dispose()
    {
        _broker?.Dispose();
        _broker = null;
    }

    /// <summary>Sets the game up before Steam's renderer is loaded into it.</summary>
    /// <param name="gameProcessId">The game process activation returned.</param>
    /// <param name="diagnostics">Whether attended-only extras were asked for.</param>
    /// <returns>What happened.</returns>
    internal RouteOutcome Prepare(int gameProcessId, bool diagnostics)
    {
        if (gameProcessId <= 0)
        {
            return new RouteOutcome(false, "Activation returned no process to set Steam up in.");
        }

        // Every payload this route loads - Steam's client and renderer, the bridge, the environment
        // stub - is x64. A 32-bit game cannot load any of it, so the route is refused rather than
        // written into a process it cannot work in.
        if (ProcessInspector.IsNativeX64(gameProcessId) is not true)
        {
            return new RouteOutcome(false,
                "The game is not a native 64-bit process, and the overlay components are 64-bit only, "
                + "so nothing was loaded into it.");
        }

        if (!File.Exists(BridgePath))
        {
            return new RouteOutcome(false,
                $"The overlay bridge is missing from this install ({Path.GetFileName(BridgePath)}).");
        }

        if (!SteamInstallation.ComponentsPresent(out var missing))
        {
            return new RouteOutcome(false,
                $"Steam's own components are not where Steam says they are: {missing} is missing.");
        }

        // A game that already has the renderer cannot be set up: the bridge has to hook object
        // creation before the renderer makes its objects, and there is no second chance.
        if (GameInjector.HasModule(gameProcessId, "GameOverlayRenderer64.dll"))
        {
            return new RouteOutcome(false,
                "Steam's renderer is already loaded in this game, so the bridge cannot be installed "
                + "ahead of it. This needs a freshly activated game.");
        }

        var session = SteamInstallation.SessionVariables();
        if (session.Count == 0)
        {
            return new RouteOutcome(false,
                "This wrapper carries no Steam session variables, so it was not started by Steam.");
        }

        if (diagnostics)
        {
            // Consumed by the bridge, which installs its file hooks only when it is set.
            injector.SetEnvironment(gameProcessId, ["WSGM_BRIDGE_DIAGNOSTICS=1"]);
        }

        _broker = OverlayObjectBroker.Start(gameProcessId, session, injector);
        if (_broker is null)
        {
            return new RouteOutcome(false, "The overlay object broker could not be started.");
        }

        if (!injector.Load(gameProcessId, BridgePath))
        {
            return new RouteOutcome(false, "The overlay bridge could not be loaded into the game.");
        }

        // Called outside DllMain on purpose: it installs hooks and maps a view, neither of which is
        // safe under the loader lock.
        var initialized = injector.Call(gameProcessId, BridgePath, "InitializeBridge");
        if (initialized != 0)
        {
            return new RouteOutcome(false,
                $"The overlay bridge did not initialize (result {Describe(initialized)}). Steam's "
                + "renderer is deliberately not loaded: without the bridge it would register with "
                + "Steam and still draw nothing.");
        }

        if (!injector.SetEnvironment(gameProcessId, session))
        {
            return new RouteOutcome(false, "Steam's session could not be carried into the game.");
        }

        if (!injector.LoadAll(gameProcessId, SteamInstallation.ClientStack)
            || !injector.Load(gameProcessId, SteamInstallation.OverlayRenderer))
        {
            return new RouteOutcome(false, "Steam's components could not be loaded into the game.");
        }

        // After the renderer, because it checks that a direct statics request reaches Steam before
        // it routes anything.
        var input = injector.Call(gameProcessId, BridgePath, "InitializeInputBridge");
        if (input != 0)
        {
            // The overlay still works. This is the controller, and saying so precisely is the
            // difference between a known limitation and a mystery.
            PackagedLaunchLog.Warn(
                $"The gamepad activation bridge did not initialize (result {Describe(input)}). The "
                + "overlay is unaffected; this game's engine may not be one the bridge covers.");
            return new RouteOutcome(true,
                $"The overlay bridge is active in process {gameProcessId}, without Steam Input.", true);
        }

        return new RouteOutcome(true, $"The overlay bridge is active in process {gameProcessId}.");
    }

    /// <summary>Reports what the bridge did, once the session is over.</summary>
    /// <param name="gameProcessId">The game process.</param>
    /// <remarks>
    ///     <para>
    ///         Two numbers worth having. The routed count separates "the input bridge was installed"
    ///         from "the game actually asked and was answered", which are not the same thing.
    ///     </para>
    ///     <para>
    ///         The initially-owned mutex count answers the open design question: the spike could not
    ///         say whether Steam ever asks for a mutex it wants to own outright, and the bridge
    ///         refuses that case rather than compromising over it. A non-zero count here means the
    ///         case is real and needs a design, and is exactly the evidence that was missing.
    ///     </para>
    /// </remarks>
    internal void Report(int gameProcessId)
    {
        if (_broker is null || injector.Latched(gameProcessId))
        {
            return;
        }

        if (injector.Call(gameProcessId, BridgePath, "InputBridgeRoutes") is { } routed)
        {
            PackagedLaunchLog.Info($"The gamepad activation bridge answered {routed} query/queries.");
        }

        if (injector.Call(gameProcessId, BridgePath, "BridgeInitialOwnerRequested") is { } owned
            && owned > 0)
        {
            PackagedLaunchLog.Warn(
                $"Steam's renderer asked {owned} time(s) for a mutex it wanted to own outright, which "
                + "the bridge refuses because that cannot be brokered safely. If the overlay "
                + "misbehaved in this session, this is the reason, and it is the observation the "
                + "design of that path was waiting on.");
        }
    }

    private static string Describe(uint? result)
    {
        return result?.ToString(CultureInfo.InvariantCulture) ?? "no answer";
    }
}
