using System;

namespace WSGM.Shell;

/// <summary>A package that could not be loaded, and whether the code it had already constructed was cleaned up.</summary>
/// <param name="message">Why loading failed.</param>
/// <param name="cleanupConfirmed">
///     False when a constructed plugin's disposal or the unload failed. Its code then stays loaded, and its
///     instance stays reserved.
/// </param>
/// <param name="inner">The original failure, when there was one.</param>
internal sealed class PluginLoadException(string message, bool cleanupConfirmed, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>Whether everything the load created was released.</summary>
    internal bool CleanupConfirmed { get; } = cleanupConfirmed;
}
