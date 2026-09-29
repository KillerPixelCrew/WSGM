using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Input;
using WSGM.Input;

namespace WSGM.Shell;

/// <summary>
///     The physical haptic return path from WSGM to the active plugin.
/// </summary>
/// <remarks>
///     The plugin owns the motors. This reports what it published for the pad it currently reads, and
///     drops frames while it has published nothing, the way HC only vibrates a controller it has plugged.
/// </remarks>
internal sealed class PluginHapticSink : IPhysicalHapticSink
{
    private readonly Func<HapticOutputFrame, CancellationToken, Task> _applyAsync;
    private volatile HapticCapabilities? _capabilities;

    internal PluginHapticSink(Func<HapticOutputFrame, CancellationToken, Task> applyAsync)
    {
        ArgumentNullException.ThrowIfNull(applyAsync);
        _applyAsync = applyAsync;
    }

    /// <inheritdoc />
    public bool IsOwned => _capabilities is not null;

    /// <inheritdoc />
    /// <remarks>
    ///     Every channel unsupported while unowned, so a frame that races the withdrawal is clamped to
    ///     silence rather than delivered at full strength.
    /// </remarks>
    public HapticCapabilities Capabilities => _capabilities ?? new HapticCapabilities();

    /// <inheritdoc />
    public Task ApplyAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return _capabilities is null ? Task.CompletedTask : _applyAsync(frame, cancellationToken);
    }

    /// <summary>Records what the plugin published for the pad it now reads.</summary>
    /// <param name="capabilities">The published capabilities, or null when it drives no haptics.</param>
    internal void Publish(HapticCapabilities? capabilities)
    {
        _capabilities = capabilities;
    }

    /// <summary>Stops passing frames to the plugin once the coordinator detaches from it.</summary>
    internal void Withdraw()
    {
        _capabilities = null;
    }
}
