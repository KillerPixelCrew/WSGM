using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Transports;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Worker;

/// <summary>The worker stopped answering or exited; whatever was in flight is uncertain.</summary>
/// <param name="message">What happened.</param>
internal sealed class LabWorkerLostException(string message) : InvalidOperationException(message);

/// <summary>
///     The wizard's side of <see cref="LabWorkerHost" />: starts the worker in a kill-on-close job,
///     authenticates it, and turns service interfaces into proxies whose calls run in the worker.
/// </summary>
/// <remarks>
///     A call that misses its deadline ends the worker, and the caller gets
///     <see cref="LabWorkerLostException" />: the write is uncertain and is never retried; the machine
///     record from before it lets the next start undo it. Closing the client closes the worker's input,
///     which zeroes streamed outputs and disposes every service.
/// </remarks>
internal sealed class LabWorkerClient : IDisposable
{
    /// <summary>Deadline for one call.</summary>
    public static readonly TimeSpan CallDeadline = TimeSpan.FromSeconds(60);

    private readonly WorkerJobObject _job;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<LabWorkerResponse>> _pending = new();
    private readonly Process _process;
    private readonly Lock _send = new();
    private volatile string? _lost;
    private long _next;

    private LabWorkerClient(Process process, WorkerJobObject job)
    {
        _process = process;
        _job = job;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                _job.TerminateAndWaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
        {
            // The worker is gone already; the job closes with this client.
        }
        finally
        {
            _job.Dispose();
            _process.Dispose();
        }
    }

    /// <summary>Starts and authenticates the worker. Blocking; call off the UI thread.</summary>
    /// <returns>The client.</returns>
    public static LabWorkerClient Start()
    {
        var secret = SelfWorkerAuthorization.CreateSecret();
        var job = WorkerJobObject.Create();
        using AnonymousPipeServerStream authorization = new(PipeDirection.Out, HandleInheritability.Inheritable);
        ProcessStartInfo start = new(DeviceLabExecutable.CurrentPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(LabWorkerHost.Mode);
        start.ArgumentList.Add("--authorization-handle");
        start.ArgumentList.Add(authorization.GetClientHandleAsString());
        Process process = new() { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The Device Lab hardware worker did not start.");
            }

            job.Assign(process);
            authorization.DisposeLocalCopyOfClientHandle();
            authorization.Write(secret);
            authorization.Flush();
            // The worker reads the secret up to end of stream before it says hello, so the pipe has to
            // close now; held open until after the hello, both sides waited on each other forever.
            // ReSharper disable once DisposeOnUsingVariable
            authorization.Dispose();
        }
        catch
        {
            process.Dispose();
            job.Dispose();
            throw;
        }

        LabWorkerClient client = new(process, job);
        _ = process.StandardError.BaseStream.CopyToAsync(System.IO.Stream.Null);
        process.StandardInput.AutoFlush = true;
        process.StandardInput.WriteLine(SelfWorkerAuthorization.Hash(secret));
        var hello = process.StandardOutput.ReadLine();
        if (hello is null || JsonSerializer.Deserialize<LabWorkerResponse>(hello, LabWorkerHost.WireOptions) is not
                { Ok: true })
        {
            client.Dispose();
            throw new InvalidOperationException("The Device Lab hardware worker did not authenticate.");
        }

        _ = Task.Run(client.ReadLoop);
        return client;
    }

    /// <summary>Opens a service in the worker and returns a proxy for its interface.</summary>
    /// <typeparam name="T">Service interface.</typeparam>
    /// <param name="service">Service name.</param>
    /// <param name="log">Where the service's log entries are copied.</param>
    /// <param name="args">Open arguments.</param>
    /// <returns>The proxy; disposing it closes the service.</returns>
    public T Open<T>(string service, LabPowerLog? log, params object?[] args)
        where T : class, IDisposable
    {
        var response = Send(new LabWorkerRequest
        {
            Op = "open",
            Service = service,
            Args =
            [
                .. args.Select(arg =>
                    JsonSerializer.SerializeToElement(arg, arg?.GetType() ?? typeof(object), LabProject.JsonOptions))
            ]
        }, log);
        var proxy = DispatchProxy.Create<T, LabWorkerProxy>();
        ((LabWorkerProxy)(object)proxy).Attach(this, response.Session, log);
        return proxy;
    }

    /// <summary>
    ///     The checkpoint handshake: the worker captures the original state, <paramref name="persist" />
    ///     records it durably, and only then does the worker accept writes on the service.
    /// </summary>
    /// <typeparam name="TState">The snapshot type.</typeparam>
    /// <param name="service">A proxy from <see cref="Open{T}" />.</param>
    /// <param name="persist">Records the original; it must finish within five seconds.</param>
    /// <returns>
    ///     The original state, which is the default of <typeparamref name="TState" /> when the snapshot was
    ///     null, and the token to release after a verified restore.
    /// </returns>
    public (TState Original, string Token) Checkpoint<TState>(object service, Action<TState> persist)
    {
        var proxy = (LabWorkerProxy)service;
        var response = Send(new LabWorkerRequest { Op = "checkpoint", Session = proxy.Session }, proxy.Log);
        var original = Snapshot<TState>(response.Result);
        persist(original);
        Send(new LabWorkerRequest { Op = "ack", Session = proxy.Session, Token = response.Token }, proxy.Log);
        return (original, response.Token!);
    }

    /// <summary>Reads a checkpoint's original; a null snapshot, sent as no result, is the type's default.</summary>
    /// <typeparam name="TState">The snapshot type.</typeparam>
    /// <param name="result">The reply's result.</param>
    /// <returns>The original.</returns>
    internal static TState Snapshot<TState>(JsonElement? result)
    {
        return LabWorkerHost.Result(result) is { } value
            ? value.Deserialize<TState>(LabProject.JsonOptions)!
            : default!;
    }

    /// <summary>
    ///     Why the service's streamed output stopped, or null while it runs. Streamed frames get no reply,
    ///     so the wizard asks.
    /// </summary>
    /// <param name="service">A proxy from <see cref="Open{T}" />.</param>
    /// <returns>The worker's failure, or null.</returns>
    public string? StreamError(object service)
    {
        var proxy = (LabWorkerProxy)service;
        var response = Send(new LabWorkerRequest { Op = "stream-status", Session = proxy.Session }, proxy.Log);
        return response.Result is { ValueKind: JsonValueKind.String } error ? error.GetString() : null;
    }

    /// <summary>Ends a checkpoint after the original was restored and read back.</summary>
    /// <param name="service">The proxy.</param>
    /// <param name="token">Token from <see cref="Checkpoint{TState}" />.</param>
    public void Release(object service, string token)
    {
        var proxy = (LabWorkerProxy)service;
        Send(new LabWorkerRequest { Op = "release", Session = proxy.Session, Token = token }, proxy.Log);
    }

    // Cancelling asks the worker to end the call's wait; the reply is still awaited, because a write the
    // call already sent is not undone by cancelling.
    internal LabWorkerResponse Send(LabWorkerRequest request, LabPowerLog? log,
        CancellationToken cancellationToken = default)
    {
        if (_lost is { } lost)
        {
            throw new LabWorkerLostException(lost);
        }

        var id = Interlocked.Increment(ref _next);
        TaskCompletionSource<LabWorkerResponse> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        var line = JsonSerializer.Serialize(request with { Id = id }, LabWorkerHost.WireOptions);
        try
        {
            lock (_send)
            {
                _process.StandardInput.WriteLine(line);
            }
        }
        catch (IOException ex)
        {
            _pending.TryRemove(id, out _);
            throw Lose($"The hardware worker's input closed: {ex.Message}");
        }

        using var cancel = cancellationToken.Register(() => ThreadPool.QueueUserWorkItem(_ => Cancel(id)));
        if (!reply.Task.Wait(CallDeadline))
        {
            _pending.TryRemove(id, out _);
            throw Lose(
                $"The hardware worker did not answer {request.Method ?? request.Op} within {CallDeadline.TotalSeconds:0} seconds.");
        }

        var response = reply.Task.Result;
        Copy(response, log);
        if (!response.Ok)
        {
            throw Rethrow(response);
        }

        return response;
    }

    internal void Stream(long session, string method, object?[] args)
    {
        if (_lost is not null)
        {
            return;
        }

        var line = JsonSerializer.Serialize(new LabWorkerRequest
        {
            Id = 0,
            Op = "stream",
            Session = session,
            Method = method,
            Args =
            [
                .. args.Select(arg =>
                    JsonSerializer.SerializeToElement(arg, arg?.GetType() ?? typeof(object), LabProject.JsonOptions))
            ]
        }, LabWorkerHost.WireOptions);
        try
        {
            lock (_send)
            {
                _process.StandardInput.WriteLine(line);
            }
        }
        catch (IOException)
        {
            // A lost worker zeroed its outputs when its input closed.
        }
    }

    private void Cancel(long id)
    {
        if (_lost is not null || !_pending.ContainsKey(id))
        {
            return;
        }

        var line = JsonSerializer.Serialize(new LabWorkerRequest { Id = id, Op = "cancel" },
            LabWorkerHost.WireOptions);
        try
        {
            lock (_send)
            {
                _process.StandardInput.WriteLine(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The call ends with the worker; its reply or the lost worker reports it.
        }
    }

    private LabWorkerLostException Lose(string message)
    {
        _lost = message;
        try
        {
            _job.TerminateAndWaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is Win32Exception or ObjectDisposedException)
        {
            // The job closes with the client at the latest.
        }

        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new LabWorkerLostException(message));
        }

        return new LabWorkerLostException(message);
    }

    private void ReadLoop()
    {
        try
        {
            while (_process.StandardOutput.ReadLine() is { } line)
            {
                var response = JsonSerializer.Deserialize<LabWorkerResponse>(line, LabWorkerHost.WireOptions);
                if (response is not null && _pending.TryRemove(response.Id, out var reply))
                {
                    reply.TrySetResult(response);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException)
        {
            // Handled below: the worker is gone.
        }

        _lost ??= "The hardware worker exited.";
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new LabWorkerLostException(_lost));
        }
    }

    private static void Copy(LabWorkerResponse response, LabPowerLog? log)
    {
        if (log is null)
        {
            return;
        }

        foreach (var entry in response.Log)
        {
            log.Add(entry.Kind, entry.Data);
        }
    }

    // The worker's failure comes back as the same kind of exception, so existing handlers still apply.
    private static Exception Rethrow(LabWorkerResponse response)
    {
        var message = response.Error ?? "The hardware worker refused the request.";
        return response.ErrorType switch
        {
            "System.ComponentModel.Win32Exception" => new Win32Exception(message),
            "System.IO.IOException" => new IOException(message),
            "System.TimeoutException" => new TimeoutException(message),
            "System.UnauthorizedAccessException" => new UnauthorizedAccessException(message),
            "System.ArgumentOutOfRangeException" => new ArgumentOutOfRangeException(null, message),
            "System.ArgumentException" => new ArgumentException(message),
            "System.Management.ManagementException" => new IOException(message),
            "System.OperationCanceledException" or "System.Threading.Tasks.TaskCanceledException" =>
                new OperationCanceledException(message),
            _ when response.ErrorType == typeof(LabRumbleRouteGoneException).FullName =>
                new LabRumbleRouteGoneException(message),
            _ => new InvalidOperationException(message)
        };
    }
}

/// <summary>Forwards interface calls to a worker session.</summary>
internal class LabWorkerProxy : DispatchProxy
{
    private LabWorkerClient? _client;

    internal long Session { get; private set; }

    internal LabPowerLog? Log { get; private set; }

    internal void Attach(LabWorkerClient client, long session, LabPowerLog? log)
    {
        _client = client;
        Session = session;
        Log = log;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var client = _client ?? throw new InvalidOperationException("The proxy is not attached.");
        args ??= [];
        if (targetMethod.Name == nameof(IDisposable.Dispose) && targetMethod.DeclaringType == typeof(IDisposable))
        {
            try
            {
                client.Send(new LabWorkerRequest { Op = "close", Session = Session }, Log);
            }
            catch (InvalidOperationException)
            {
                // A lost worker already closed every session.
            }

            return null;
        }

        if (targetMethod.GetCustomAttribute<LabWorkerStreamAttribute>() is not null)
        {
            client.Stream(Session, targetMethod.Name, args);
            return null;
        }

        // A CancellationToken stays here and cancels the call in the worker; everything else is sent.
        var parameters = targetMethod.GetParameters();
        var cancellationToken = args.OfType<CancellationToken>().FirstOrDefault();
        var response = client.Send(new LabWorkerRequest
        {
            Op = "call",
            Session = Session,
            Method = targetMethod.Name,
            Args =
            [
                .. parameters.Select((parameter, i) => (parameter, i))
                    .Where(item => item.parameter.ParameterType != typeof(CancellationToken))
                    .Select(item => JsonSerializer.SerializeToElement(args[item.i], item.parameter.ParameterType,
                        LabProject.JsonOptions))
            ]
        }, Log, cancellationToken);
        return targetMethod.ReturnType == typeof(void) || response.Result is not { } result
            ? null
            : result.Deserialize(targetMethod.ReturnType, LabProject.JsonOptions);
    }
}
