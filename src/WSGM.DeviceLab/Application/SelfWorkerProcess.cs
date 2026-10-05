using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

namespace WSGM.DeviceLab.Application;

/// <summary>Starts a Device Lab self-worker: the hardware worker and the disposable read-probe worker.</summary>
/// <remarks>
///     The caller fills the start information (executable, mode arguments and redirects) and owns the
///     process, the job and every failure path; this does only the shared steps, in this order: add
///     <c>--authorization-handle</c>, start, join the kill-on-close job, then write the one-use secret and
///     close the pipe before returning, so the worker reads it to EOF within
///     <see cref="SelfWorkerAuthorization.AuthorizationDeadline" /> and the caller can wait for its greeting.
/// </remarks>
internal static class SelfWorkerProcess
{
    /// <summary>Starts the worker and delivers its secret. Blocking.</summary>
    /// <param name="process">The process, with its start information filled in.</param>
    /// <param name="job">The kill-on-close job the worker joins.</param>
    /// <param name="secret">The one-use authorization secret.</param>
    /// <exception cref="InvalidOperationException">The process did not start.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">Starting or joining the job failed.</exception>
    /// <exception cref="System.IO.IOException">The secret could not be written.</exception>
    internal static void Start(Process process, WorkerJobObject job, ReadOnlySpan<byte> secret)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(job);
        using AnonymousPipeServerStream authorization = new(PipeDirection.Out, HandleInheritability.Inheritable);
        process.StartInfo.ArgumentList.Add("--authorization-handle");
        process.StartInfo.ArgumentList.Add(authorization.GetClientHandleAsString());
        if (!process.Start())
        {
            throw new InvalidOperationException("The Device Lab self-worker did not start.");
        }

        job.Assign(process);
        authorization.DisposeLocalCopyOfClientHandle();
        authorization.Write(secret);
        authorization.Flush();
    }
}
