using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Worker;

/// <summary>A hardware service the worker may host: its interface, which is the call allowlist, and how to open it.</summary>
/// <param name="Name">Service name used by <c>open</c>.</param>
/// <param name="Interface">Interface whose methods are the only ones callable.</param>
/// <param name="Open">Opens the service from its arguments, logging into the given log.</param>
internal sealed record LabWorkerService(
    string Name,
    Type Interface,
    Func<IReadOnlyList<JsonElement>, LabPowerLog, object> Open)
{
    /// <summary>Reads one open argument as its declared type.</summary>
    /// <typeparam name="T">The argument type.</typeparam>
    /// <param name="args">The open arguments.</param>
    /// <param name="index">Which one.</param>
    /// <returns>The argument.</returns>
    public static T Arg<T>(IReadOnlyList<JsonElement> args, int index)
    {
        return index < args.Count
            ? args[index].Deserialize<T>(LabProject.JsonOptions)!
            : throw new ArgumentException($"The service needs open argument {index}.");
    }
}

/// <summary>
///     The elevated hardware worker the wizard starts once per session. Every hardware write the wizard
///     makes runs here, behind the checkpoint handshake ported from AllyXLab: before the first write on a
///     service, the worker captures the original state itself, hands it to the wizard, and writes nothing
///     until the wizard confirms it has recorded it, within five seconds.
/// </summary>
/// <remarks>
///     The worker is authenticated like Device Lab's other self-workers (a secret over an inherited
///     pipe, echoed as a hash on the first line), lives in a kill-on-close job the wizard owns, and ends
///     when its input closes. Only the methods of a registered service's interface can be called, with
///     arguments bound to their declared types; there is no generic register or report access. Streamed
///     outputs are put back to rest when frames stop for <see cref="StreamTimeout" />.
/// </remarks>
internal static class LabWorkerHost
{
    /// <summary>Command-line mode.</summary>
    public const string Mode = "__lab-worker";

    /// <summary>How long a streamed output may go without a frame before the worker zeroes it.</summary>
    public static readonly TimeSpan StreamTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>How long the wizard has to acknowledge a checkpoint.</summary>
    public static readonly TimeSpan AcknowledgeTimeout = TimeSpan.FromSeconds(5);

    private static readonly Lock Output = new();

    /// <summary>One-line JSON for the pipe, with the project's naming.</summary>
    internal static JsonSerializerOptions WireOptions { get; } = new(LabProject.JsonOptions) { WriteIndented = false };

    /// <summary>Runs the worker until its input closes.</summary>
    /// <param name="args">Worker arguments.</param>
    /// <returns>Exit code.</returns>
    public static int Run(IReadOnlyList<string> args)
    {
        var handle = Option(args, "--authorization-handle");
        var secret = handle is null
            ? null
            : SelfWorkerAuthorization.ReadSecretAsync(handle, CancellationToken.None).GetAwaiter().GetResult();
        if (secret is null)
        {
            return 64;
        }

        var hello = Console.In.ReadLine();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(hello ?? string.Empty),
                Encoding.ASCII.GetBytes(SelfWorkerAuthorization.Hash(secret))))
        {
            return 64;
        }

        Write(new LabWorkerResponse { Id = 0, Ok = true });
        Dictionary<long, LabWorkerSession> sessions = [];
        LabWorkerCalls calls = new();
        using BlockingCollection<LabWorkerRequest> queue = new();
        using Timer watchdog = new(_ => ZeroStale(sessions), null, 100, 100);

        // Requests run in order on one thread, so this loop can still read a cancel while a call waits.
        Thread dispatcher = new(() => Dispatch(queue, sessions, calls))
        {
            IsBackground = true,
            Name = "Device Lab worker requests"
        };
        dispatcher.Start();
        try
        {
            while (Console.In.ReadLine() is { } line)
            {
                LabWorkerRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<LabWorkerRequest>(line, LabProject.JsonOptions);
                }
                catch (JsonException ex)
                {
                    Write(new LabWorkerResponse
                        { Id = -1, Ok = false, Error = ex.Message, ErrorType = nameof(JsonException) });
                    continue;
                }

                if (request is null)
                {
                    continue;
                }

                if (request.Op == "cancel")
                {
                    calls.Cancel(request.Id);
                    continue;
                }

                queue.Add(request);
            }
        }
        finally
        {
            // Input closed: the wizard is done or gone. End any wait, finish what was queued, then put
            // every streamed output at rest and close.
            calls.CancelAll();
            queue.CompleteAdding();
            dispatcher.Join();
            lock (sessions)
            {
                foreach (var session in sessions.Values)
                {
                    session.ZeroQuietly();
                    session.Dispose();
                }

                sessions.Clear();
            }
        }

        return 0;
    }

    private static void Dispatch(BlockingCollection<LabWorkerRequest> queue,
        Dictionary<long, LabWorkerSession> sessions, LabWorkerCalls calls)
    {
        long next = 0;
        foreach (var request in queue.GetConsumingEnumerable())
        {
            // Streamed frames and their status polls are high-rate and never logged.
            var logged = request.Op is not ("stream" or "stream-status");
            var what = $"{request.Op} {request.Service ?? $"session {request.Session}"} {request.Method}".TrimEnd();
            if (logged)
            {
                LabTrace.Write($"worker {what}: start");
            }

            lock (sessions)
            {
                Handle(request, sessions, calls, ref next);
            }

            if (logged)
            {
                LabTrace.Write($"worker {what}: returned");
            }
        }
    }

    private static void Handle(LabWorkerRequest request, Dictionary<long, LabWorkerSession> sessions,
        LabWorkerCalls calls, ref long next)
    {
        LabPowerLog opening = new();
        LabWorkerSession? session = null;
        try
        {
            switch (request.Op)
            {
                case "open":
                {
                    var service = LabWorkerServices.All.GetValueOrDefault(request.Service ?? string.Empty)
                                  ?? throw new InvalidOperationException(
                                      $"Unknown worker service '{request.Service}'.");
                    var id = ++next;
                    session = new LabWorkerSession(service, service.Open(request.Args, opening), opening);
                    sessions[id] = session;
                    Reply(request, session.TakeLog(), session: id);
                    return;
                }
                case "stream":
                {
                    if (sessions.TryGetValue(request.Session, out var streamed))
                    {
                        streamed.Stream(request.Method!, request.Args);
                    }

                    return;
                }
            }

            session = sessions.GetValueOrDefault(request.Session)
                      ?? throw new InvalidOperationException("The worker session is not open.");
            switch (request.Op)
            {
                case "call":
                {
                    var call = calls.Begin(request.Id);
                    JsonElement? result;
                    try
                    {
                        result = session.Call(request.Method!, request.Args, call.Token);
                    }
                    finally
                    {
                        calls.End(call);
                    }

                    Reply(request, session.TakeLog(), result);
                    return;
                }
                case "stream-status":
                    Reply(request, session.TakeLog(),
                        session.StreamError is { } failed ? JsonSerializer.SerializeToElement(failed) : null);
                    return;
                case "checkpoint":
                {
                    var (token, original) = session.Checkpoint();
                    Reply(request, session.TakeLog(), original, token: token);
                    return;
                }
                case "ack":
                    session.Acknowledge(request.Token);
                    Reply(request, session.TakeLog());
                    return;
                case "release":
                    session.Release(request.Token);
                    Reply(request, session.TakeLog());
                    return;
                case "close":
                    session.ZeroQuietly();
                    session.Dispose();
                    sessions.Remove(request.Session);
                    Reply(request, session.TakeLog());
                    return;
                default:
                    throw new InvalidOperationException($"Unknown worker operation '{request.Op}'.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var inner = ex is TargetInvocationException { InnerException: { } cause } ? cause : ex;
            LabTrace.Write($"worker {request.Op} {request.Method}: failed, {LabTrace.Describe(inner)}");
            Write(new LabWorkerResponse
            {
                Id = request.Id,
                Ok = false,
                Error = inner.Message,
                ErrorType = inner.GetType().FullName,
                Log = session?.TakeLog() ?? LabWorkerSession.Entries(opening.Events())
            });
        }
    }

    /// <summary>A null return value or snapshot as no result, so both ends agree on what null looks like.</summary>
    /// <param name="result">The serialized value.</param>
    /// <returns>The value, or null.</returns>
    internal static JsonElement? Result(JsonElement? result)
    {
        return result is { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } ? null : result;
    }

    private static void Reply(LabWorkerRequest request, IReadOnlyList<LabWorkerLogEntry> log,
        JsonElement? result = null, long session = 0, string? token = null)
    {
        Write(new LabWorkerResponse
        {
            Id = request.Id,
            Ok = true,
            Result = Result(result),
            Session = session,
            Token = token,
            Log = log
        });
    }

    private static void Write(LabWorkerResponse response)
    {
        var line = JsonSerializer.Serialize(response, WireOptions);
        lock (Output)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }

    private static void ZeroStale(Dictionary<long, LabWorkerSession> sessions)
    {
        lock (sessions)
        {
            foreach (var session in sessions.Values)
            {
                session.ZeroIfStale();
            }
        }
    }

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
