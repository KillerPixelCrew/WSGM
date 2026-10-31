using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     The sandboxed scene host process: its medium-integrity launch, the verified pipe, the job that contains
///     it, and the reader that reports what the player says.
/// </summary>
/// <remarks>
///     One player runs at a time. The owner serializes <see cref="StartAsync" /> and <see cref="StopAsync" />,
///     and never starts another player before a stop has released the previous one.
/// </remarks>
internal sealed class ScreensaverPlayerProcess
{
    private readonly UserDataContext _context;
    private readonly CancellationToken _lifetime;
    private bool _contained;
    private Process? _helper;
    private WorkerJobObject? _job;
    private NamedPipeServerStream? _pipe;
    private Task _reader = Task.CompletedTask;
    private CancellationTokenSource? _reading;

    /// <summary>Creates the owner of the scene host; nothing starts until <see cref="StartAsync" />.</summary>
    /// <param name="context">The user data context whose profile folder the host uses.</param>
    /// <param name="lifetime">The owning session's lifetime; its end stops the reader.</param>
    internal ScreensaverPlayerProcess(UserDataContext context, CancellationToken lifetime)
    {
        _context = context;
        _lifetime = lifetime;
    }

    /// <summary>Starts the host, verifies its identity, contains it and hands it the playback.</summary>
    /// <param name="playback">What to play; its window handle is set to Big Picture's here.</param>
    /// <param name="current">Whether this start is still the owner's latest; a stale reader reports nothing.</param>
    /// <param name="playing">Called when the player reports that it is playing.</param>
    /// <param name="ended">
    ///     Called once when the player closes, fails or loses its pipe, with the error to show (or null) and
    ///     whether input dismissed it.
    /// </param>
    /// <param name="token">Cancels the launch and the verification.</param>
    /// <returns>A task completing once the player reports that it is playing, or failing when it ended first.</returns>
    internal async Task<Task> StartAsync(ScreensaverPlayback playback, Func<bool> current, Action playing,
        Action<string?, bool> ended, CancellationToken token)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Screensaver", "WSGM.ScreensaverHost.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The screensaver host is missing. Repair or update WSGM.");
        }

        _job = WorkerJobObject.Create();
        var pipeName = "WSGM.Screensaver." + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("Current Windows account is unavailable.");
        var security = new PipeSecurity();
        security.SetSecurityDescriptorSddlForm("D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;" + sid + ")");
        _pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, ScreensaverWire.MaximumPacketBytes, ScreensaverWire.MaximumPacketBytes,
            security, HandleInheritability.None, PipeAccessRights.TakeOwnership);
        BrowserNative.SetMediumPipeLabel(_pipe.SafePipeHandle);
        using var parent = Process.GetCurrentProcess();
        var arguments = LaunchArguments.Join(new[]
        {
            pipeName, Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            Path.Combine(_context.Root, "ScreensaverProfile")
        });
        var connection = _pipe.WaitForConnectionAsync(token);
        if (ElevationCheck.IsCurrentProcessElevated() != false)
        {
            var launched = await UnelevatedLauncher.TryStartViaScheduledTaskAsync(_context, executable, arguments,
                DateTimeOffset.UtcNow.AddSeconds(20), Path.GetDirectoryName(executable), token).ConfigureAwait(false);
            if (launched == ScheduledTaskLaunchDisposition.NotDispatched)
            {
                throw new InvalidOperationException("Windows could not start the screensaver at medium integrity.");
            }
        }
        else
        {
            _helper = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)!
            }) ?? throw new InvalidOperationException("The screensaver host did not start.");
        }

        await connection.ConfigureAwait(false);
        var hello = await ScreensaverWire.ReadAsync(_pipe, token).ConfigureAwait(false);
        if (hello.Kind != "hello" || !BrowserNative.GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var clientId)
            || clientId != hello.ProcessId || !BrowserNative.IsMediumIntegrity(hello.ProcessId))
        {
            throw new InvalidDataException("Screensaver host identity or medium integrity could not be verified.");
        }

        _helper ??= Process.GetProcessById(hello.ProcessId);
        if (_helper.Id != hello.ProcessId
            || !string.Equals(_helper.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The pipe is not connected to the installed screensaver host.");
        }

        _job.Assign(_helper);
        _contained = true;
        _reading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _reader = ReadAsync(_pipe, current, confirmed, playing, ended, _reading.Token);
        playback.WindowHandle = Steam.FindBigPictureWindow();
        await ScreensaverWire.WriteAsync(_pipe, playback, token).ConfigureAwait(false);
        return confirmed.Task;
    }

    /// <summary>Asks the player to close, then ends its job and releases the pipe and the process.</summary>
    /// <returns>False when the job or the reader could not be released; no second player may start then.</returns>
    internal async Task<bool> StopAsync()
    {
        try
        {
            if (_pipe?.IsConnected == true && _helper is { HasExited: false })
            {
                using var close = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await ScreensaverWire.WriteAsync(_pipe, new ScreensaverPlayback { Kind = "close" }, close.Token)
                    .ConfigureAwait(false);
                try
                {
                    await _helper.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException)
        {
        }

        if (!_contained && _helper is { HasExited: false })
        {
            _helper.Kill(entireProcessTree: true);
            await _helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        if (_job is not null && !await _job.TerminateAndWaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false))
        {
            return false;
        }

        _reading?.Cancel();
        _pipe?.Dispose();
        try
        {
            await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }

        _reading?.Dispose();
        _reading = null;
        _pipe = null;
        _helper?.Dispose();
        _helper = null;
        _job?.Dispose();
        _job = null;
        _contained = false;
        return true;
    }

    private static async Task ReadAsync(Stream pipe, Func<bool> current, TaskCompletionSource confirmed, Action playing,
        Action<string?, bool> ended, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var message = await ScreensaverWire.ReadAsync(pipe, token).ConfigureAwait(false);
                if (!current())
                {
                    return;
                }

                if (message.Kind == "playing")
                {
                    confirmed.TrySetResult();
                    playing();
                    continue;
                }

                if (message.Kind is "closed" or "error")
                {
                    confirmed.TrySetException(new IOException(message.Error ?? "The screensaver player closed."));
                    ended(message.Kind == "error" ? message.Error : null, message.Error == "input");
                    return;
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            confirmed.TrySetException(error);
            if (!token.IsCancellationRequested && current())
            {
                ended("The custom player exited: " + error.Message, false);
            }
        }
    }
}
