using System.Diagnostics;
using WSGM.Core;

namespace WSGM.Launch;

/// <summary>Starts the medium target only after its whole process tree is contained.</summary>
internal static class SuspendedProcess
{
    internal static (Process Process, JobObject Job) Start(ProcessStartInfo start)
    {
        var job = JobObject.Create();
        try
        {
            var process = ContainedProcessStart.Start(start, handle =>
            {
                job.Assign(handle);
                return true;
            });
            return (process, job);
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    internal static string BuildEnvironment(ProcessStartInfo start)
    {
        return ContainedProcessStart.BuildEnvironment(start);
    }
}
