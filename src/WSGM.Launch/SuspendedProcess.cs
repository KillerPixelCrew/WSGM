using System.Diagnostics;
using WSGM.Core;

namespace WSGM.Launch;

/// <summary>Starts the medium target only after its whole process tree is contained.</summary>
internal static class SuspendedProcess
{
    /// <summary>Creates, contains and resumes a target using an explicit environment and argument vector.</summary>
    /// <param name="start">Executable, ArgumentList, working directory and complete child environment.</param>
    /// <param name="primaryToken">Borrowed limited primary token, or zero for the caller's token.</param>
    /// <returns>The process and job handles; the caller owns and must dispose both.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">Creation, job assignment or resumption failed.</exception>
    /// <remarks>Failure after creation attempts to terminate the suspended target before releasing ownership.</remarks>
    internal static (Process Process, JobObject Job) Start(ProcessStartInfo start, nint primaryToken = 0)
    {
        var job = JobObject.Create();
        try
        {
            var process = ContainedProcessStart.Start(start, handle =>
            {
                job.Assign(handle);
                return true;
            }, primaryToken);
            return (process, job);
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    /// <summary>Builds the sorted, double-NUL-terminated Unicode environment block for CreateProcessW.</summary>
    /// <param name="start">The complete child environment to encode.</param>
    /// <returns>Windows environment entries including the required final terminator.</returns>
    /// <exception cref="System.ArgumentException">A variable name or value contains a NUL character.</exception>
    internal static string BuildEnvironment(ProcessStartInfo start)
    {
        return ContainedProcessStart.BuildEnvironment(start);
    }
}
