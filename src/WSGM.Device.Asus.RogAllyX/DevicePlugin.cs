// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Asus.RogAllyX;

/// <summary>Passive ROG Ally X scaffold awaiting exact identity and hardware validation.</summary>
public sealed class DevicePlugin : IDevicePlugin
{
    private static readonly CapabilityReason ScaffoldReason = new(
        CapabilityReasonCode.Unsupported,
        "ROG Ally X support is not implemented; exact identity and hardware validation are pending.");

    /// <inheritdoc />
    public string PackageId => "wsgm.device.asus.rog-ally-x";

    /// <inheritdoc />
    public ValueTask<PluginDetectionResult> DetectAsync(
        PluginDetectionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Add an exact predicate only after an attended inventory establishes the supported device.
        return ValueTask.FromResult(new PluginDetectionResult { Matched = false, Reason = ScaffoldReason });
    }

    /// <inheritdoc />
    public ValueTask<PluginStartResult> StartAsync(
        PluginStartContext context,
        CancellationToken cancellationToken)
    {
        PluginTrace.Install(context.Host);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(PassiveResult());
    }

    /// <inheritdoc />
    public ValueTask<CapabilityCommandResult> ExecuteCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Rejected,
            Reason = ScaffoldReason,
            CompletedAt = DateTimeOffset.UtcNow
        });
    }

    /// <inheritdoc />
    public ValueTask SuspendAsync(PluginQuiesceContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<PluginStartResult> ResumeAsync(
        PluginResumeContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(PassiveResult());
    }

    /// <inheritdoc />
    public ValueTask<PluginDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PluginDiagnostics
        {
            Values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["implementation"] = "scaffold",
                ["identity-validation"] = "pending",
                ["hardware-acquired"] = "false"
            }
        });
    }

    /// <inheritdoc />
    public ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // No output channels or physical controller are declared; drop every frame.
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<PluginControllerRelease> ReleaseControllerAsync(
        PluginControllerReleaseContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PluginControllerRelease
        {
            Step = ControllerHandoffStep.NotStarted,
            Result = ControllerHandoffResult.ReleasedUnverified
        });
    }

    /// <inheritdoc />
    public ValueTask SetControllerManagementAsync(
        PluginControllerManagementContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<PluginStopResult> StopAsync(PluginStopContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Nothing was acquired or changed, so there is no restoration to perform.
        return ValueTask.FromResult(new PluginStopResult { Status = PluginStopStatus.Clean });
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static PluginStartResult PassiveResult() => new()
    {
        State = PluginOperationalState.Passive,
        Reason = ScaffoldReason
    };
}
