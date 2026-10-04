using System;
using System.Collections.Generic;
using WSGM.Core;
using WSGM.Device.Sdk.Input;

namespace WSGM.Shell;

/// <summary>Reference-counted controller capture for WSGM's visible surfaces.</summary>
internal sealed class UiCaptureState
{
    private readonly HashSet<string> _surfaces = new(StringComparer.Ordinal);
    private CanonicalButtons _withheldFromGame;

    /// <summary>Whether any WSGM surface currently holds capture.</summary>
    internal bool IsCaptured => _surfaces.Count > 0;

    /// <summary>Claims capture and remembers controls held before the first surface opened.</summary>
    /// <returns><see langword="true" /> when this claim started capture.</returns>
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
