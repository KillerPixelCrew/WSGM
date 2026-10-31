using System.IO;

namespace WSGM.Core;

/// <summary>Where a tool is installed for one game.</summary>
/// <param name="AppId">The game.</param>
/// <param name="InstallRoot">The game's current install root.</param>
/// <param name="RuntimeExecutable">The absolute runtime executable; its folder is the target directory.</param>
/// <param name="Architecture">The runtime executable's architecture.</param>
/// <param name="Engine">The detected engine id.</param>
internal sealed record GameToolTarget(uint AppId, string InstallRoot, string RuntimeExecutable, string Architecture,
    string Engine)
{
    /// <summary>The absolute directory the tool is installed into.</summary>
    internal string TargetDirectory => Path.GetDirectoryName(RuntimeExecutable)!;

    /// <summary>The target directory relative to the install root, or absolute outside it.</summary>
    internal string RelativeTargetDirectory => GameToolPaths.RelativeDirectory(InstallRoot, TargetDirectory);

    /// <summary>The target a manifest was written for, resolved against the game's current install root.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="installRoot">The game's current install root.</param>
    /// <param name="manifest">The manifest; its paths are relative to the root.</param>
    /// <returns>The target.</returns>
    /// <remarks>
    ///     Configure, remove, update, repair and recovery act where the tool is, never where the program chosen now
    ///     sits.
    /// </remarks>
    internal static GameToolTarget FromManifest(uint appId, string installRoot, GameToolManifest manifest)
    {
        return new GameToolTarget(appId, installRoot, GameToolPaths.Resolve(installRoot, manifest.RuntimeExecutable),
            manifest.Architecture, manifest.Engine);
    }

    /// <summary>The target a fresh install uses: beside the program the folder's resolution chose.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="installRoot">The game's current install root.</param>
    /// <param name="resolution">The folder's resolution, which names the engine.</param>
    /// <param name="runtime">The chosen program.</param>
    /// <returns>The target.</returns>
    internal static GameToolTarget FromRuntime(uint appId, string installRoot, GameRuntimeResolution resolution,
        GameRuntimeCandidate runtime)
    {
        return new GameToolTarget(appId, installRoot, runtime.FullPath, runtime.Architecture, resolution.Engine);
    }
}
