using System;
using System.Collections.Generic;
using System.Text.Json;

namespace WSGM.DeviceLab.Worker;

/// <summary>Marks an interface method that changes hardware state; it needs an acknowledged checkpoint.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class LabWorkerWriteAttribute : Attribute;

/// <summary>
///     Marks the parameterless method the worker calls to capture the original state for a checkpoint.
///     One per service.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class LabWorkerSnapshotAttribute : Attribute;

/// <summary>
///     Marks a one-way method streamed at a steady rate, such as live rumble levels. It gets no reply, and
///     the worker calls the service's <see cref="LabWorkerZeroAttribute" /> method when frames stop.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class LabWorkerStreamAttribute : Attribute;

/// <summary>Marks the parameterless method that puts a streamed output back to rest.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class LabWorkerZeroAttribute : Attribute;

/// <summary>One request from the wizard to the worker.</summary>
internal sealed record LabWorkerRequest
{
    /// <summary>Request ID; zero for a streamed frame, which gets no reply.</summary>
    public long Id { get; init; }

    /// <summary><c>open</c>, <c>call</c>, <c>checkpoint</c>, <c>ack</c>, <c>release</c>, <c>stream</c> or <c>close</c>.</summary>
    public required string Op { get; init; }

    /// <summary>Service name, for <c>open</c>.</summary>
    public string? Service { get; init; }

    /// <summary>Session ID, for everything after <c>open</c>.</summary>
    public long Session { get; init; }

    /// <summary>Interface method name, for <c>call</c> and <c>stream</c>.</summary>
    public string? Method { get; init; }

    /// <summary>Arguments, one JSON value per parameter.</summary>
    public IReadOnlyList<JsonElement> Args { get; init; } = [];

    /// <summary>Checkpoint token, for <c>ack</c> and <c>release</c>.</summary>
    public string? Token { get; init; }
}

/// <summary>One reply from the worker.</summary>
internal sealed record LabWorkerResponse
{
    /// <summary>The request it answers.</summary>
    public long Id { get; init; }

    /// <summary>Whether the request succeeded.</summary>
    public bool Ok { get; init; }

    /// <summary>The return value, when there is one.</summary>
    public JsonElement? Result { get; init; }

    /// <summary>The failure, when it failed.</summary>
    public string? Error { get; init; }

    /// <summary>The failure's exception type name, so the wizard can rethrow the same kind.</summary>
    public string? ErrorType { get; init; }

    /// <summary>The new session, for <c>open</c>.</summary>
    public long Session { get; init; }

    /// <summary>The checkpoint token, for <c>checkpoint</c>.</summary>
    public string? Token { get; init; }

    /// <summary>Log entries the service wrote while handling the request.</summary>
    public IReadOnlyList<LabWorkerLogEntry> Log { get; init; } = [];
}

/// <summary>A service log entry carried back to the wizard.</summary>
/// <param name="At">When.</param>
/// <param name="Kind">What.</param>
/// <param name="Data">Details as JSON.</param>
internal sealed record LabWorkerLogEntry(DateTimeOffset At, string Kind, JsonElement? Data);
