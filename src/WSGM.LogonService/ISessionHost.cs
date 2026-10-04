using System;
using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.LogonService;

/// <summary>The Windows operations used by one logon service instance.</summary>
internal interface ISessionHost
{
    bool HasPendingSetup { get; }
    int LastError { get; }
    bool TryGetUserToken(uint sessionId, out nint token);
    BootManifest? ReadManifest(nint userToken);
    bool TryGetElevationType(nint token, out int elevationType);
    nint GetLinkedPrimaryToken(nint token, uint sessionId);

    bool TryLaunch(nint token, string executable, string arguments, out nint process, out uint processId,
        out int error);

    IEnumerable<(uint SessionId, TimeSpan LogonAge)> ActiveSessions();
    string GetSessionUser(uint sessionId);
    uint WaitForSingleObject(nint process, uint milliseconds);
    bool TryGetExitCode(nint process, out uint exitCode);
    bool IsSessionActive(uint sessionId);
    bool IsDesktopShellInSession(nint userToken, string executable);
    void CloseHandle(nint handle);
    void StartWatchdog(Action watch, string name);
    void WaitForAnchor(TimeSpan grace);
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}
