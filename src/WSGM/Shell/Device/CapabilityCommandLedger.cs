using System;
using System.Collections.Generic;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>What the latest command per capability left behind: its id, pending value, value and result.</summary>
/// <remarks>
///     Only the latest command of a capability may settle its projection; a result for an older one is
///     dropped. Not synchronized: the owning router reads and writes it under its own gate.
/// </remarks>
internal sealed class CapabilityCommandLedger
{
    private readonly Dictionary<DeviceCapabilityKey, CapabilityValue> _lastCommandValues = [];
    private readonly Dictionary<DeviceCapabilityKey, CapabilityCommandResult> _lastResults = [];
    private readonly Dictionary<DeviceCapabilityKey, Guid> _latestCommands = [];
    private readonly Dictionary<DeviceCapabilityKey, CapabilityValue> _pendingValues = [];

    /// <summary>Records a command as the latest for its capability, before admission decides it.</summary>
    /// <param name="key">The capability instance.</param>
    /// <param name="commandId">The command.</param>
    internal void Begin(DeviceCapabilityKey key, Guid commandId)
    {
        _latestCommands[key] = commandId;
    }

    /// <summary>Records an admitted command's value as pending and as the value last commanded.</summary>
    /// <param name="key">The capability instance.</param>
    /// <param name="value">The requested value, or null for an action, which commands no value.</param>
    internal void Admit(DeviceCapabilityKey key, CapabilityValue? value)
    {
        if (value is not null)
        {
            _pendingValues[key] = value;
            _lastCommandValues[key] = value;
        }
        else
        {
            _lastCommandValues.Remove(key);
        }
    }

    /// <summary>Settles a result when it belongs to the latest command of its capability.</summary>
    /// <param name="key">The capability instance.</param>
    /// <param name="result">The command's result.</param>
    /// <param name="terminal">Whether no late completion follows, which ends the pending value.</param>
    /// <returns>False when a newer command superseded this one and the result was dropped.</returns>
    internal bool Complete(DeviceCapabilityKey key, CapabilityCommandResult result, bool terminal)
    {
        if (!_latestCommands.TryGetValue(key, out var latest) || latest != result.CommandId)
        {
            return false;
        }

        if (terminal)
        {
            _pendingValues.Remove(key);
        }

        _lastResults[key] = result;
        return true;
    }

    /// <summary>Forgets pending values and latest commands, so no in-flight result settles any more.</summary>
    internal void ForgetInFlight()
    {
        _pendingValues.Clear();
        _latestCommands.Clear();
    }

    /// <summary>Forgets every record, for a new publisher or descriptor set.</summary>
    internal void Clear()
    {
        ForgetInFlight();
        _lastResults.Clear();
        _lastCommandValues.Clear();
    }

    /// <summary>Reads what a capability's latest command left behind.</summary>
    /// <param name="key">The capability instance.</param>
    /// <returns>The pending value, the last result and the value last commanded, each null when absent.</returns>
    internal (CapabilityValue? Pending, CapabilityCommandResult? Result, CapabilityValue? Commanded) Read(
        DeviceCapabilityKey key)
    {
        _pendingValues.TryGetValue(key, out var pending);
        _lastResults.TryGetValue(key, out var result);
        _lastCommandValues.TryGetValue(key, out var commanded);
        return (pending, result, commanded);
    }

    /// <summary>The command progress a pending value and last result present.</summary>
    /// <param name="pending">The pending value, or null.</param>
    /// <param name="result">The last result, or null.</param>
    /// <returns>Pending while a value is pending, otherwise the last outcome's progress.</returns>
    internal static CommandProgress Progress(
        CapabilityValue? pending,
        CapabilityCommandResult? result)
    {
        if (pending is not null)
        {
            return CommandProgress.Pending;
        }

        return result?.Outcome switch
        {
            { } outcome when outcome.IsApplied() => CommandProgress.Completed,
            CommandOutcome.TimedOut or CommandOutcome.Indeterminate => CommandProgress.Uncertain,
            CommandOutcome.Rejected => CommandProgress.Failed,
            _ => CommandProgress.Idle
        };
    }
}
