using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Presentation snapshot of one plugin instance; does not transfer lifecycle ownership to the view.</summary>
/// <param name="Identity">Package and host instance identity.</param>
/// <param name="Name">Package display name.</param>
/// <param name="Generation">Current admitted generation, or zero before registration.</param>
/// <param name="Controls">Admitted control declarations, or null before they are available.</param>
/// <param name="Status">Plain lifecycle and health text.</param>
/// <param name="CanInvoke">Whether host admission currently permits actions; dispatch still revalidates.</param>
/// <param name="Error">Retained load or runtime error, or null when none is recorded.</param>
internal sealed record PluginOverlayInstance(
    PluginInstanceIdentity Identity,
    string Name,
    long Generation,
    PluginOverlayControls? Controls,
    string Status,
    bool CanInvoke,
    string? Error)
{
    /// <summary>Manifest category used for grouping; empty when no category was projected.</summary>
    internal string Category { get; init; } = string.Empty;
}
