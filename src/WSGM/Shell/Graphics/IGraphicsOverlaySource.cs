using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>The closed source the Graphics destination and the Graphics page in Steam read and write.</summary>
internal interface IGraphicsOverlaySource : IDisposable
{
    /// <summary>Raised when page state may have changed; coordinator notifications use the UI dispatcher.</summary>
    /// <remarks>A stale-row refusal raises this on the write caller's thread. UI subscribers must dispatch when needed.</remarks>
    event Action? Changed;

    /// <summary>The pages as they should be drawn now.</summary>
    /// <returns>The snapshot.</returns>
    GraphicsOverlaySnapshot Snapshot();

    /// <summary>Writes one graphics capability as the user.</summary>
    /// <param name="capability">The row the user changed, as last drawn.</param>
    /// <param name="value">The new value, or null to run an action.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The plugin's answer, or null when the row no longer matches what the plugin publishes.</returns>
    Task<CapabilityCommandResult?> WriteAsync(
        DeviceOverlayCapability capability,
        CapabilityValue? value,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a graphics setting to Global for the running game.</summary>
    /// <param name="overrideId">The id the row carried in <see cref="DeviceOverlayCapability.OverrideId" />.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>Whether an override was removed.</returns>
    Task<bool> UseGlobalAsync(string overrideId, CancellationToken cancellationToken = default);
}
