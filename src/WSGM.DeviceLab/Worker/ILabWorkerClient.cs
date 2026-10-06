using System;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Worker;

/// <summary>The worker operations used by checkpointed hardware workflows and recovery.</summary>
internal interface ILabWorkerClient
{
    /// <summary>Opens a registered hardware service.</summary>
    /// <typeparam name="T">The service interface.</typeparam>
    /// <param name="service">The registered service name.</param>
    /// <param name="log">Receives the service's log entries.</param>
    /// <param name="args">The service's open arguments.</param>
    /// <returns>The service proxy.</returns>
    T Open<T>(string service, LabPowerLog? log, params object?[] args) where T : class, IDisposable;

    /// <summary>Records the original before acknowledging the checkpoint and allowing writes.</summary>
    /// <typeparam name="TState">The snapshot type.</typeparam>
    /// <param name="service">The service proxy.</param>
    /// <param name="persist">Records the snapshot.</param>
    /// <returns>The original and the checkpoint token.</returns>
    (TState Original, string Token) Checkpoint<TState>(object service, Action<TState> persist);

    /// <summary>Releases the matching checkpoint after restoration.</summary>
    /// <param name="service">The service proxy.</param>
    /// <param name="token">The checkpoint token.</param>
    void Release(object service, string token);

    /// <summary>Returns the failure of a streamed output, if any.</summary>
    /// <param name="service">The service proxy.</param>
    /// <returns>The failure, or null.</returns>
    string? StreamError(object service);
}
