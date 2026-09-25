using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Worker;

/// <summary>
///     One open service in the worker: the instance, its checkpoint state and its stream watchdog. It holds
///     the checkpoint rules, so they can be tested without a process.
/// </summary>
/// <remarks>
///     A <see cref="LabWorkerWriteAttribute" /> method is refused until a checkpoint is acknowledged. Only
///     one checkpoint is open at a time; a second is refused until the first is released. An acknowledgement
///     later than <see cref="LabWorkerHost.AcknowledgeTimeout" /> is refused and nothing may be written.
/// </remarks>
/// <param name="service">The service.</param>
/// <param name="instance">The opened instance; disposed with the session.</param>
/// <param name="log">The log the instance writes into.</param>
/// <param name="clock">The UTC clock; <see cref="DateTime.UtcNow" /> by default.</param>
internal sealed class LabWorkerSession(
    LabWorkerService service,
    object instance,
    LabPowerLog log,
    Func<DateTime>? clock = null) : IDisposable
{
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private string? _armed;
    private DateTime _lastFrame = DateTime.MaxValue;
    private string? _pending;
    private DateTime _pendingSince;
    private int _sent;

    /// <summary>Whether writes are accepted now.</summary>
    public bool Armed => _armed is not null;

    /// <summary>
    ///     Why a streamed frame failed since the checkpoint was acknowledged, or null. Later frames are
    ///     ignored until a new checkpoint, so the wizard polls this to learn the stream stopped.
    /// </summary>
    public string? StreamError { get; private set; }

    /// <inheritdoc />
    public void Dispose()
    {
        (instance as IDisposable)?.Dispose();
    }

    /// <summary>The log entries written since the last call, for the next reply.</summary>
    /// <returns>The new entries.</returns>
    public IReadOnlyList<LabWorkerLogEntry> TakeLog()
    {
        var events = log.Events();
        var fresh = events.Skip(_sent).ToArray();
        _sent = events.Count;
        return Entries(fresh);
    }

    /// <summary>Converts log events to wire entries.</summary>
    /// <param name="events">The events.</param>
    /// <returns>The entries.</returns>
    public static IReadOnlyList<LabWorkerLogEntry> Entries(IEnumerable<LabPowerEvent> events)
    {
        return
        [
            .. events.Select(item => new LabWorkerLogEntry(item.At, item.Kind,
                item.Data is null
                    ? null
                    : JsonSerializer.SerializeToElement(item.Data, item.Data.GetType(), LabProject.JsonOptions)))
        ];
    }

    /// <summary>Calls one interface method.</summary>
    /// <param name="name">Method name.</param>
    /// <param name="args">One JSON value per parameter, leaving out a <see cref="CancellationToken" />.</param>
    /// <param name="cancellationToken">Bound to a <see cref="CancellationToken" /> parameter, if the method has one.</param>
    /// <returns>The return value, or null for a void method.</returns>
    public JsonElement? Call(string name, IReadOnlyList<JsonElement> args,
        CancellationToken cancellationToken = default)
    {
        var method = Method(name);
        if (method.GetCustomAttribute<LabWorkerWriteAttribute>() is not null && _armed is null)
        {
            throw new InvalidOperationException(
                $"{service.Name}.{name} changes hardware and needs an acknowledged checkpoint first.");
        }

        var result = method.Invoke(instance, Bind(method, args, cancellationToken));
        return method.ReturnType == typeof(void)
            ? null
            : JsonSerializer.SerializeToElement(result, method.ReturnType, LabProject.JsonOptions);
    }

    /// <summary>Delivers one streamed frame; a method without the stream attribute is ignored.</summary>
    /// <param name="name">Method name.</param>
    /// <param name="args">Arguments.</param>
    public void Stream(string name, IReadOnlyList<JsonElement> args)
    {
        var method = Method(name);
        if (method.GetCustomAttribute<LabWorkerStreamAttribute>() is null || _armed is null || StreamError is not null)
        {
            return;
        }

        _lastFrame = _clock();
        try
        {
            method.Invoke(instance, Bind(method, args, CancellationToken.None));
        }
        catch (TargetInvocationException ex)
        {
            // A failed frame is uncertain. Zero once and ignore later slider frames.
            StreamError = ex.InnerException?.Message ?? ex.Message;
            log.Add("stream-failed", StreamError);
            ZeroQuietly();
        }
    }

    /// <summary>Captures the original state and opens a checkpoint that waits for its acknowledgement.</summary>
    /// <returns>The token and the original.</returns>
    public (string Token, JsonElement Original) Checkpoint()
    {
        if (_armed is not null || _pending is not null)
        {
            throw new InvalidOperationException("A checkpoint is already open for this service; release it first.");
        }

        var snapshot = service.Interface.GetMethods()
                           .SingleOrDefault(item => item.GetCustomAttribute<LabWorkerSnapshotAttribute>() is not null)
                       ?? throw new InvalidOperationException($"{service.Name} has no snapshot method.");
        var original = JsonSerializer.SerializeToElement(snapshot.Invoke(instance, []), snapshot.ReturnType,
            LabProject.JsonOptions);
        _pending = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _pendingSince = _clock();
        return (_pending, original);
    }

    /// <summary>Arms writes once the wizard has recorded the original.</summary>
    /// <param name="token">The checkpoint token.</param>
    public void Acknowledge(string? token)
    {
        if (_pending is null || token != _pending)
        {
            throw new InvalidOperationException("The checkpoint acknowledgement does not match.");
        }

        if (_clock() - _pendingSince > LabWorkerHost.AcknowledgeTimeout)
        {
            _pending = null;
            throw new InvalidOperationException(
                "The checkpoint was not acknowledged within five seconds; nothing was written.");
        }

        _armed = _pending;
        _pending = null;
        StreamError = null;
    }

    /// <summary>Ends the checkpoint after a verified restore; writes are refused again.</summary>
    /// <param name="token">The checkpoint token.</param>
    public void Release(string? token)
    {
        if (_armed is not null && token == _armed)
        {
            _armed = null;
        }
    }

    /// <summary>Zeroes a streamed output that has gone quiet.</summary>
    public void ZeroIfStale()
    {
        if (_lastFrame != DateTime.MaxValue && _clock() - _lastFrame > LabWorkerHost.StreamTimeout)
        {
            ZeroQuietly();
        }
    }

    /// <summary>Puts a streamed output at rest, logging a failure instead of throwing.</summary>
    public void ZeroQuietly()
    {
        _lastFrame = DateTime.MaxValue;
        var zero = service.Interface.GetMethods()
            .FirstOrDefault(item => item.GetCustomAttribute<LabWorkerZeroAttribute>() is not null);
        try
        {
            zero?.Invoke(instance, []);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is IOException or InvalidOperationException
                                                       or Win32Exception)
        {
            log.Add("zero-failed", ex.InnerException.Message);
        }
    }

    private MethodInfo Method(string name)
    {
        return service.Interface.GetMethods().SingleOrDefault(item => item.Name == name)
               ?? throw new InvalidOperationException($"{service.Name} has no method '{name}'.");
    }

    // A CancellationToken parameter is not sent over the wire; it is the call's own token.
    private static object?[] Bind(MethodInfo method, IReadOnlyList<JsonElement> args,
        CancellationToken cancellationToken)
    {
        var parameters = method.GetParameters();
        var sent = parameters.Count(parameter => parameter.ParameterType != typeof(CancellationToken));
        if (sent != args.Count)
        {
            throw new ArgumentException($"{method.Name} takes {sent} arguments.");
        }

        var next = 0;
        return
        [
            .. parameters.Select(parameter => parameter.ParameterType == typeof(CancellationToken)
                ? cancellationToken
                : args[next++].Deserialize(parameter.ParameterType, LabProject.JsonOptions))
        ];
    }
}
