using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace WSGM.Deelevate;

internal static class ScheduledTaskLauncher
{
    internal static string? Start(string executablePath, string pipeName)
    {
        var suffix = $"{Environment.ProcessId}-{Guid.NewGuid():N}";
        var taskName = $"WSGM_Deelevate_{suffix}";
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WSGM");
        var xmlPath = Path.Combine(directory, $"deelevate-task-{suffix}.xml");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(xmlPath, BuildTaskXml(executablePath, pipeName), Encoding.Unicode);
            if (!RunSchtasks(["/Create", "/TN", taskName, "/XML", xmlPath, "/F"]) ||
                !RunSchtasks(["/Run", "/TN", taskName]))
            {
                Delete(taskName);
                return null;
            }

            DeelevateLog.Info($"Started medium-integrity helper task {taskName}.");
            return taskName;
        }
        catch (Exception ex)
        {
            DeelevateLog.Error($"Could not start medium-integrity helper task: {ex.Message}");
            Delete(taskName);
            return null;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    internal static void Delete(string? taskName)
    {
        if (string.IsNullOrEmpty(taskName))
        {
            return;
        }
        _ = RunSchtasks(["/Delete", "/TN", taskName, "/F"], logFailure: false);
    }

    internal static string BuildTaskXml(string executablePath, string pipeName)
    {
        var user = WindowsIdentity.GetCurrent().Name;
        var command = SecurityElement.Escape(executablePath);
        var arguments = SecurityElement.Escape($"--medium-child {pipeName}");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
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
                  <Command>{command}</Command>
                  <Arguments>{arguments}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static bool RunSchtasks(string[] arguments, bool logFailure = true)
    {
        try
        {
            var startInfo = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null || !process.WaitForExit(15_000) || process.ExitCode != 0)
            {
                if (logFailure)
                {
                    DeelevateLog.Error($"schtasks {arguments[0]} failed or timed out.");
                }
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            if (logFailure)
            {
                DeelevateLog.Error($"schtasks {arguments[0]} failed: {ex.Message}");
            }
            return false;
        }
    }
}
