using System;
using System.Diagnostics;
using System.IO;

namespace OpenFSE.Core;

/// <summary>Starts a process with the interactive user's medium-IL token from an
/// elevated OpenFSE, by registering and running a one-shot scheduled task with
/// LogonType=InteractiveToken and default (least) run level — the same mechanism
/// Windows 11's own explorer uses to de-elevate itself (CreateExplorerShellUnelevatedTask).
///
/// The naive TokenLinkedToken route does NOT work here: without SeTcbPrivilege the
/// linked token is only a SecurityIdentification impersonation token and cannot be
/// converted to a primary token (fails with ERROR_BAD_IMPERSONATION_LEVEL) — verified
/// empirically. When UAC is disabled entirely there is no limited token to run as and
/// this (like every technique) cannot help.</summary>
internal static class UnelevatedLauncher
{
    private const string TaskName = "OpenFSE_StartUnelevated";

    public static bool TryStartViaScheduledTask(string exePath)
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), "openfse-task.xml");
        try
        {
            // Task Scheduler expects canonical UTF-16 task XML; a UTF-8 file is
            // rejected with "cannot switch encoding" (verified locally).
            File.WriteAllText(xmlPath, BuildTaskXml(exePath), System.Text.Encoding.Unicode);

            if (!RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F"))
            {
                return false;
            }
            var started = RunSchtasks($"/Run /TN \"{TaskName}\"");
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
            if (started)
            {
                Log.Info($"Started via de-elevating scheduled task: {exePath}");
            }
            return started;
        }
        catch (Exception ex)
        {
            Log.Error("De-elevated launch via scheduled task failed", ex);
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    private static string BuildTaskXml(string exePath)
    {
        // InteractiveToken principal without a RunLevel element = the user's
        // filtered medium-IL token (RunLevel defaults to LeastPrivilege).
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Principals>
                <Principal id="Author">
                  <UserId>{System.Security.SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                </Principal>
              </Principals>
              <Settings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{System.Security.SecurityElement.Escape(exePath)}</Command>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static bool RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p is null)
        {
            return false;
        }
        p.WaitForExit(15_000);
        if (p.ExitCode != 0)
        {
            Log.Warn($"schtasks {arguments.Split(' ')[0]} exited with {p.ExitCode}.");
            return false;
        }
        return true;
    }
}
