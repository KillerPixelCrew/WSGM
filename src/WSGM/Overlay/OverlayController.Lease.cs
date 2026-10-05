using WSGM.Core;
using WSGM.Input;

namespace WSGM.Overlay;

public sealed partial class OverlayController
{
    /// <summary>Lets WSGM's own navigation and the chord read the managed controller.</summary>
    /// <param name="pad">The managed controller's UI state.</param>
    internal void UseManagedPad(ManagedUiPad pad)
    {
        _gamepad.UseManagedPad(pad);
    }

    /// <summary>Claims this controller's Steam Input lease for a focus-taking surface.</summary>
    /// <remarks>
    ///     The lease blocks Steam's controller access only while SDL needs direct input for the sheet,
    ///     then lets Steam rediscover the controller after the last surface closes.
    /// </remarks>
    private void AcquireSteamInputLease()
    {
        // User opt-out: never touch Steam at all. The config watcher replaces
        // _config wholesale on reload, so a change is picked up without a restart —
        // but it is read HERE, at the top of an open, so it takes effect at the NEXT
        // surface open, not on the surface already on screen. A lease already applied
        // is deliberately NOT released when the opt-out arrives mid-surface: the
        // release hands the pad back to Steam's desktop profile, which per docs\steam-input.md
        // swallows it from SDL system-wide, so a controller user who turned this off
        // from the open Settings window would lose navigation on the very click that
        // saved it. The lease is scoped to the surface lifetime by specification
        // (docs\steam-input.md, Overlay\AGENTS.md): acquire before a surface opens,
        // release only after the last one closes. Controller input in a panel opened
        // with the opt-out active then depends on what Steam's desktop profile
        // leaves us.
        if (!_config.SteamInputLeaseEnabled)
        {
            Log.Info("Steam Input lease disabled in settings — surface opens without blocking Steam Input.");
            return;
        }

        // Deliberately NOT gated on SteamInputBlocker.IsApplied: the lease is
        // shared by the process's surfaces, so "applied" can just as well mean ANOTHER owner holds it
        // (the settings window this panel opened). Claiming it under our own name is
        // what stops that owner's release from leaving this surface unblocked;
        // see docs\steam-input.md. Joining a live lease costs no release/re-inject churn.
        _steamInput.Hold(_leaseOwner);
    }

    /// <summary>
    ///     Ends this controller's claim. Dispose ends it early, so the deferred Closed handler
    ///     cannot tear down a replacement controller's live surface; a second call is a no-op.
    ///     The blocker only really lets go of the lease when no other owner still claims it.
    /// </summary>
    private void ReleaseSteamInputLease()
    {
        _leaseRelease = _steamInput.Drop(_leaseOwner, "surface-closed");
    }

    private void ClaimUiSurface(string surfaceId)
    {
        if (_uiSurfaces.Add(surfaceId))
        {
            UiSurfaceOpened?.Invoke(surfaceId);
            return;
        }

        Log.Change(
            $"ui-surface.{surfaceId}",
            $"Managed UI capture claim skipped because {surfaceId} already owns it.");
    }

    private void ReleaseUiSurface(string surfaceId)
    {
        if (_uiSurfaces.Remove(surfaceId))
        {
            UiSurfaceClosed?.Invoke(surfaceId);
            return;
        }

        Log.Change(
            $"ui-surface.{surfaceId}",
            $"Managed UI capture release skipped because {surfaceId} has no claim.");
    }
}
