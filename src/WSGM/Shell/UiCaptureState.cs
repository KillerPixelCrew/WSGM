using System;
using System.Collections.Generic;
using WSGM.Core;
using WSGM.Device.Sdk.Input;

namespace WSGM.Shell;

/// <summary>Tracks one controller-capture claim per visible surface. Access must be serialized by the caller.</summary>
internal sealed class UiCaptureState
{
    private readonly HashSet<string> _surfaces = new(StringComparer.Ordinal);
    private CanonicalButtons _withheldFromGame;

    /// <summary>Whether any WSGM surface currently holds capture.</summary>
    internal bool IsCaptured => _surfaces.Count > 0;

    /// <summary>Claims capture and remembers controls held before the first surface opened.</summary>
    /// <returns><see langword="true" /> when this claim started capture.</returns>
    /// <param name="surfaceId">Unique surface identity; duplicate claims do not increase the count.</param>
    /// <param name="heldAtOpen">Buttons held when the first surface opens, withheld until released.</param>
    internal bool Claim(string surfaceId, CanonicalButtons heldAtOpen)
    {
        var wasCaptured = IsCaptured;
        if (!_surfaces.Add(surfaceId))
        {
            Log.Change(
                $"ui-capture.{surfaceId}",
                $"Managed UI capture claim ignored: surface={surfaceId}, reason=already-claimed.");
            return false;
        }

        if (wasCaptured)
        {
            return false;
        }

        _withheldFromGame = heldAtOpen;
        return true;
    }

    /// <summary>Releases a claim and reports whether the last known surface closed.</summary>
    /// <param name="surfaceId">Identity used by Claim; unknown identities are ignored.</param>
    /// <returns>True only when a known claim was removed and no surfaces remain.</returns>
    internal bool Release(string surfaceId)
    {
        if (_surfaces.Remove(surfaceId))
        {
            return !IsCaptured;
        }

        Log.Change(
            $"ui-capture.{surfaceId}",
            $"Managed UI capture release ignored: surface={surfaceId}, reason=not-claimed.");
        return false;
    }

    /// <summary>Whether a sample stays away from the game.</summary>
    /// <remarks>
    ///     While a surface holds capture, and afterwards until every control the UI was still using has
    ///     been observed up, so the game never sees a press whose start it did not see.
    /// </remarks>
    /// <param name="buttons">Current buttons; this call updates the remembered release mask.</param>
    /// <returns>True while capture is held or a button held at release is still down.</returns>
    internal bool Withholds(CanonicalButtons buttons)
    {
        if (IsCaptured)
        {
            _withheldFromGame = buttons;
            return true;
        }

        _withheldFromGame &= buttons;
        return _withheldFromGame != CanonicalButtons.None;
    }
}
