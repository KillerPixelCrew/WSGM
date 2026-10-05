// Shared between WSGM.Launch and WSGM.PackagedLaunch (linked as a source file), so every child either
// wrapper starts drops the same variable under the same rule.

using System;

namespace WSGM.Launch;

/// <summary>The variable Steam sets to hide its own virtual controllers from SDL.</summary>
/// <remarks>
///     Steam sets it for the game it launches. A child that inherits it cannot see the controller WSGM
///     gives it, so both wrappers remove it from the environment of every program they start.
/// </remarks>
internal static class SteamControllerExclusion
{
    /// <summary>The variable's name.</summary>
    internal const string Variable = "SDL_GAMECONTROLLER_IGNORE_DEVICES";

    /// <summary>Whether an environment variable is Steam's controller exclusion.</summary>
    /// <param name="name">The variable's name, compared as Windows compares names.</param>
    /// <returns>True for the exclusion.</returns>
    internal static bool Is(string name)
    {
        return string.Equals(name, Variable, StringComparison.OrdinalIgnoreCase);
    }
}
