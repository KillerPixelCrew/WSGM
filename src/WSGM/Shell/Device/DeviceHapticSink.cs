using System;
using System.Threading;
using System.Threading.Tasks;
using LibHandheld.Contracts;
using WSGM.Input;

namespace WSGM.Shell;

/// <summary>
///     The physical haptic return path from WSGM to the running handheld device.
/// </summary>
/// <remarks>
///     The device owns the motors. This reports what it published for the pad it currently reads, and
///     drops frames while it has published nothing, the way HC only vibrates a controller it has plugged.
/// </remarks>
internal sealed class DeviceHapticSink : IPhysicalHapticSink
{
    /// <summary>Every channel unsupported; immutable, so one instance serves every read.</summary>
    private static readonly HapticCapabilities Unowned = new();

    private readonly Func<HapticOutputFrame, CancellationToken, Task> _applyAsync;
    private volatile HapticCapabilities? _capabilities;

    /// <summary>Creates a haptic route that remains inactive until the device publishes motor ownership.</summary>
    /// <param name="applyAsync">Borrowed physical-output callback; frames reach it only while capabilities are owned.</param>
    internal DeviceHapticSink(Func<HapticOutputFrame, CancellationToken, Task> applyAsync)
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
    public HapticCapabilities Capabilities => _capabilities ?? Unowned;

    /// <inheritdoc />
    public Task ApplyAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
    {
        return _capabilities is null ? Task.CompletedTask : _applyAsync(frame, cancellationToken);
    }

    /// <summary>Records what the device published for the pad it now reads.</summary>
    /// <param name="capabilities">The published capabilities, or null when it drives no haptics.</param>
    internal void Publish(HapticCapabilities? capabilities)
    {
        _capabilities = capabilities;
    }

    /// <summary>Stops passing frames to the device once the coordinator detaches from it.</summary>
    internal void Withdraw()
    {
        _capabilities = null;
    }
}
