using System.Security;
using System.Security.Principal;

namespace WSGM.Core;

/// <summary>Builds the shared Task Scheduler document used for de-elevated launches.</summary>
internal static class ScheduledTaskXml
{
    internal static string Build(string executablePath, string arguments = "", string? workingDirectory = null)
    {
        // InteractiveToken principal without a RunLevel element = the user's
        // filtered medium-IL token (RunLevel defaults to LeastPrivilege).
        using var identity = WindowsIdentity.GetCurrent();
        var argumentsElement = arguments.Length == 0
            ? ""
            : $"\n                  <Arguments>{SecurityElement.Escape(arguments)}</Arguments>";
        var directoryElement = string.IsNullOrEmpty(workingDirectory)
            ? ""
            : $"\n                  <WorkingDirectory>{SecurityElement.Escape(workingDirectory)}</WorkingDirectory>";
        return $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <Principals>
                    <Principal id="Author">
                      <UserId>{SecurityElement.Escape(identity.Name)}</UserId>
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
                      <Command>{SecurityElement.Escape(executablePath)}</Command>{argumentsElement}{directoryElement}
                    </Exec>
                  </Actions>
                </Task>
                """;
    }
}
