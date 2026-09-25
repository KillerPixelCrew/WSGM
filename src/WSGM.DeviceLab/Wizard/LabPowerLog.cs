using System;
using System.Collections.Generic;
using System.Threading;

namespace WSGM.DeviceLab.Wizard;

/// <summary>One timestamped entry in the power stage's log.</summary>
/// <param name="At">When it happened.</param>
/// <param name="Kind">What happened, for example <c>acpi-write</c> or <c>readback</c>.</param>
/// <param name="Data">Details, serialized as they are.</param>
internal sealed record LabPowerEvent(DateTimeOffset At, string Kind, object? Data);

/// <summary>
///     Every write, readback and observation of the power stage, in order. Transports log each call
///     before it is made and its result after, so an interrupted run still shows what was attempted.
/// </summary>
internal sealed class LabPowerLog
{
    private readonly List<LabPowerEvent> _events = [];
    private readonly Lock _gate = new();

    /// <summary>Adds an entry stamped with the current time.</summary>
    /// <param name="kind">What happened.</param>
    /// <param name="data">Details.</param>
    public void Add(string kind, object? data = null)
    {
        lock (_gate)
        {
            _events.Add(new LabPowerEvent(DateTimeOffset.UtcNow, kind, data));
        }
    }

    /// <summary>A copy of the entries so far.</summary>
    /// <returns>The entries, oldest first.</returns>
    public IReadOnlyList<LabPowerEvent> Events()
    {
        lock (_gate)
        {
            return [.. _events];
        }
    }
}
