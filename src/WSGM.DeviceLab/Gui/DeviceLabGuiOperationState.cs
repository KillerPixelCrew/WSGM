namespace WSGM.DeviceLab.Gui;

/// <summary>Immutable projection for one-at-a-time GUI operation status and durable output.</summary>
internal sealed record DeviceLabGuiOperationState
{
    /// <summary>The last successfully serialized operation result.</summary>
    public string? LastSuccessfulResult { get; private init; }

    /// <summary>Current concise operation status.</summary>
    public required string StatusText { get; init; }

    /// <summary>Whether the operation gate is occupied.</summary>
    public bool IsRunning { get; private init; }

    /// <summary>Initial idle projection.</summary>
    public static DeviceLabGuiOperationState Initial { get; } = new() { StatusText = "Ready." };

    /// <summary>Starts work without replacing the last successful result.</summary>
    /// <returns>A running projection retaining the last successful output.</returns>
    public DeviceLabGuiOperationState Started()
    {
        return this with
        {
            StatusText = "Working…",
            IsRunning = true
        };
    }

    /// <summary>Publishes a successful immutable result.</summary>
    /// <param name="result">Serialized result to retain for display or export.</param>
    /// <returns>A completed projection containing the new result.</returns>
    public static DeviceLabGuiOperationState Succeeded(string result)
    {
        return new DeviceLabGuiOperationState
        {
            LastSuccessfulResult = result,
            StatusText = "Completed successfully.",
            IsRunning = false
        };
    }

    /// <summary>Reports cancellation without replacing the last successful result.</summary>
    /// <returns>An idle projection retaining the last successful output.</returns>
    public DeviceLabGuiOperationState Cancelled()
    {
        return this with
        {
            StatusText = "Operation cancelled.",
            IsRunning = false
        };
    }

    /// <summary>Reports failure without replacing the last successful result.</summary>
    /// <param name="message">Failure detail to show in the status text.</param>
    /// <returns>An idle failed projection retaining the last successful output.</returns>
    public DeviceLabGuiOperationState Failed(string message)
    {
        return this with
        {
            StatusText = $"Operation failed: {message}",
            IsRunning = false
        };
    }
}
