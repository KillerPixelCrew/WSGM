using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Worker;

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
/// <param name="input">Requests, one JSON line each, after the greeting.</param>
/// <param name="output">Replies, one JSON line each.</param>
/// <param name="services">The services that can be opened, by name.</param>
/// <param name="time">The clock and timer for the stream watchdog and the checkpoint deadline.</param>
internal sealed class LabWorkerHost(
    TextReader input,
    TextWriter output,
    IReadOnlyDictionary<string, LabWorkerService> services,
    TimeProvider time)
{
    /// <summary>Command-line mode.</summary>
    public const string Mode = "__lab-worker";

    /// <summary>How long a streamed output may go without a frame before the worker zeroes it.</summary>
    public static readonly TimeSpan StreamTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>How long the wizard has to acknowledge a checkpoint.</summary>
    public static readonly TimeSpan AcknowledgeTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromMilliseconds(100);

    private readonly LabWorkerCalls _calls = new();
    private readonly Lock _output = new();

    // Changed only on the request thread, which holds this lock while it runs a request. The watchdog
    // takes it with TryEnter and skips a tick while a request runs.
    private readonly Dictionary<long, LabWorkerSession> _sessions = [];
    private long _next;

    /// <summary>One-line JSON for the pipe, with the project's naming.</summary>
    internal static JsonSerializerOptions WireOptions { get; } = new(LabProject.JsonOptions) { WriteIndented = false };

    /// <summary>Authenticates the worker and runs it over the console until its input closes.</summary>
    /// <param name="args">Worker arguments.</param>
    /// <returns>Exit code.</returns>
    public static int Run(IReadOnlyList<string> args)
    {
        var handle = Option(args, "--authorization-handle");
        byte[]? secret = null;
        if (handle is not null)
        {
            using CancellationTokenSource authorization = new(SelfWorkerAuthorization.AuthorizationDeadline);
            try
            {
                secret = SelfWorkerAuthorization.ReadSecretAsync(handle, authorization.Token).GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException)
            {
                // The supervisor never delivered the secret; refuse like a wrong one.
            }
        }

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

        return new LabWorkerHost(Console.In, Console.Out, LabWorkerServices.All, TimeProvider.System).Serve();
    }

    /// <summary>Greets the wizard and serves requests until the input closes.</summary>
    /// <returns>Exit code.</returns>
    public int Serve()
    {
        Write(new LabWorkerResponse { Id = 0, Ok = true });
        using BlockingCollection<LabWorkerRequest> queue = new();
        using var watchdog = time.CreateTimer(_ => ZeroStale(), null, WatchdogInterval, WatchdogInterval);

        // Requests run in order on one thread, so this loop can still read a cancel while a call waits.
        Thread dispatcher = new(() => Dispatch(queue))
        {
            IsBackground = true,
            Name = "Device Lab worker requests"
        };
        dispatcher.Start();
        try
        {
            while (input.ReadLine() is { } line)
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
                    _calls.Cancel(request.Id);
                    continue;
                }

                queue.Add(request);
            }
        }
        finally
        {
            // Input closed: the wizard is done or gone. End any wait and drop what is still queued, so no
            // write reaches the hardware after the wizard left. Then put every streamed output at rest
            // and close; a lane is never zeroed under a running call.
            _calls.CancelAll();
            queue.CompleteAdding();
            dispatcher.Join();
            lock (_sessions)
            {
                foreach (var session in _sessions.Values)
                {
                    session.ZeroQuietly();
                    session.Dispose();
                }

                _sessions.Clear();
            }
        }

        return 0;
    }

    /// <summary>One watchdog tick: zeroes every streamed output that went quiet, unless a request is running.</summary>
    internal void ZeroStale()
    {
        // A busy lane is skipped: zeroing it would be a second write to the same device mid-call, and a
        // tick that waited would park a thread for as long as the call runs.
        if (!Monitor.TryEnter(_sessions))
        {
            return;
        }

        try
        {
            foreach (var session in _sessions.Values)
            {
                session.ZeroIfStale();
            }
        }
        finally
        {
            Monitor.Exit(_sessions);
        }
    }

    private void Dispatch(BlockingCollection<LabWorkerRequest> queue)
    {
        foreach (var request in queue.GetConsumingEnumerable())
        {
            if (_calls.Closed)
            {
                // Nobody reads a reply any more.
                continue;
            }

            // Streamed frames, their status polls and sampled reads are high-rate and never traced.
            var logged = request.Op switch
            {
                "stream" or "stream-status" => false,
                "call" => _sessions.GetValueOrDefault(request.Session)?.IsSampled(request.Method) != true,
                _ => true
            };
            var what = $"{request.Op} {request.Service ?? $"session {request.Session}"} {request.Method}".TrimEnd();
            if (logged)
            {
                LabTrace.Write($"worker {what}: start");
            }

            lock (_sessions)
            {
                Handle(request);
            }

            if (logged)
            {
                LabTrace.Write($"worker {what}: returned");
            }
        }
    }

    private void Handle(LabWorkerRequest request)
    {
        LabPowerLog opening = new();
        LabWorkerSession? session = null;
        try
        {
            switch (request.Op)
            {
                case "open":
                {
                    var service = services.GetValueOrDefault(request.Service ?? string.Empty)
                                  ?? throw new InvalidOperationException(
                                      $"Unknown worker service '{request.Service}'.");
                    var id = ++_next;
                    session = new LabWorkerSession(service, service.Open(request.Args, opening), opening,
                        () => time.GetUtcNow().UtcDateTime);
                    _sessions[id] = session;
                    Reply(request, session.TakeLog(), session: id);
                    return;
                }
                case "stream":
                {
                    if (_sessions.TryGetValue(request.Session, out var streamed))
                    {
                        streamed.Stream(request.Method!, request.Args);
                    }

                    return;
                }
            }

            session = _sessions.GetValueOrDefault(request.Session)
                      ?? throw new InvalidOperationException("The worker session is not open.");
            switch (request.Op)
            {
                case "call":
                {
                    var call = _calls.Begin(request.Id);
                    JsonElement? result;
                    try
                    {
                        result = session.Call(request.Method!, request.Args, call.Token);
                    }
                    finally
                    {
                        _calls.End(call);
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
                    _sessions.Remove(request.Session);
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

    private void Reply(LabWorkerRequest request, IReadOnlyList<LabWorkerLogEntry> log,
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

    private void Write(LabWorkerResponse response)
    {
        var line = JsonSerializer.Serialize(response, WireOptions);
        lock (_output)
        {
            output.WriteLine(line);
            output.Flush();
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
