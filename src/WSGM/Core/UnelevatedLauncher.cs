using System;
using System.IO;

namespace WSGM.Core;

/// <summary>Starts a process with the interactive user's medium-IL token from an
/// elevated WSGM, by registering and running a one-shot scheduled task with
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
    public static bool TryStartViaScheduledTask(string exePath, string arguments = "")
    {
        // Unique per invocation: a fixed name collides across concurrent launches,
        // and a stale leftover task would shadow the fresh one.
        var suffix = $"{Environment.ProcessId}-{Random.Shared.Next():x8}";
        var taskName = $"WSGM_StartUnelevated_{suffix}";
        // NOT %TEMP%: the XML is consumed by an elevated schtasks, and a fixed,
        // predictable, user-writable path could be swapped between the write and
        // /Create. Log.Directory plus the random name closes the easy version.
        var xmlPath = Path.Combine(Log.Directory, $"wsgm-task-{suffix}.xml");
        var created = false;
        try
        {
            Directory.CreateDirectory(Log.Directory);
            // Task Scheduler expects canonical UTF-16 task XML; a UTF-8 file is
            // rejected with "cannot switch encoding" (verified locally).
            File.WriteAllText(xmlPath, BuildTaskXml(exePath, arguments), System.Text.Encoding.Unicode);

            created = RunSchtasks($"/Create /TN \"{taskName}\" /XML \"{xmlPath}\" /F");
            if (!created)
            {
                return false;
            }
            var started = RunSchtasks($"/Run /TN \"{taskName}\"");
            if (started)
            {
                Log.Info($"Started via de-elevating scheduled task: {exePath}" +
                         (arguments.Length == 0 ? "" : $" {arguments}"));
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
            // Best-effort cleanup on EVERY path (including /Run failures/throws) so
            // a one-shot task never stays registered.
            if (created)
            {
                try { RunSchtasks($"/Delete /TN \"{taskName}\" /F"); } catch { }
            }
            try { File.Delete(xmlPath); } catch { }
        }
    }

    internal static string BuildTaskXml(string exePath, string arguments = "")
    {
        // InteractiveToken principal without a RunLevel element = the user's
        // filtered medium-IL token (RunLevel defaults to LeastPrivilege).
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var argumentsElement = arguments.Length == 0
            ? ""
            : $"\n                  <Arguments>{System.Security.SecurityElement.Escape(arguments)}</Arguments>";
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
                  <Command>{System.Security.SecurityElement.Escape(exePath)}</Command>{argumentsElement}
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static bool RunSchtasks(string arguments) => ConsoleTool.Run(ConsoleTool.System32("schtasks.exe"), arguments);
}
