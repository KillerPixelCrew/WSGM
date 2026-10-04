using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using WSGM.Core;
using WSGM.Install;
using WSGM.Interop;
using WSGM.LogonService.Interop;

namespace WSGM.LogonService;

/// <summary>The production Windows implementation of the service's session operations.</summary>
internal sealed class WindowsSessionHost : ISessionHost
{
    private const uint WaitObject0 = 0;
    public bool HasPendingSetup => InstallLayout.HasPendingSetup;
    public int LastError => Marshal.GetLastWin32Error();

    public bool TryGetUserToken(uint sessionId, out nint token)
    {
        return NativeMethods.WTSQueryUserToken(sessionId, out token);
    }

    public BootManifest? ReadManifest(nint userToken)
    {
        var profile = GetUserProfileDirectory(userToken);
        return profile is null
            ? null
            : BootManifestStore.TryLoad(Path.Combine(profile, "AppData", "Local", "WSGM",
                BootManifestStore.FileName));
    }

    public bool TryGetElevationType(nint token, out int elevationType)
    {
        return NativeMethods.GetTokenInformationDword(token, NativeMethods.TokenElevationTypeClass,
            out elevationType, sizeof(int), out _);
    }

    public nint GetLinkedPrimaryToken(nint userToken, uint sessionId)
    {
        if (!NativeMethods.GetTokenInformationHandle(userToken, NativeMethods.TokenLinkedTokenClass,
                out var linked, (uint)nint.Size, out _))
        {
            ServiceLog.Warn(
                $"Session {sessionId}: TokenLinkedToken query failed (error {Marshal.GetLastWin32Error()}) — launching unelevated.");
            return 0;
        }

        try
        {
            if (!Win32Common.DuplicateTokenEx(linked, NativeMethods.MaximumAllowed, 0,
                    NativeMethods.SecurityImpersonation, NativeMethods.TokenPrimary, out var primary))
            {
                ServiceLog.Warn(
                    $"Session {sessionId}: DuplicateTokenEx failed (error {Marshal.GetLastWin32Error()}) — launching unelevated.");
                return 0;
            }

            // Defensive: pin the primary token to the target session (legal under
            // SeTcbPrivilege). A failure only logs — the token usually already
            // carries the right session id.
            var sid = sessionId;
            if (!NativeMethods.SetTokenInformation(primary, NativeMethods.TokenSessionIdClass, ref sid, sizeof(uint)))
            {
                ServiceLog.Warn(
                    $"Session {sessionId}: SetTokenInformation(TokenSessionId) failed (error {Marshal.GetLastWin32Error()}).");
            }

            return primary;
        }
        finally
        {
            Win32Common.CloseHandle(linked);
        }
    }

    public uint WaitForSingleObject(nint process, uint milliseconds)
    {
        return Win32Common.WaitForSingleObject(process, milliseconds);
    }

    public bool TryGetExitCode(nint process, out uint exitCode)
    {
        return NativeMethods.GetExitCodeProcess(process, out exitCode);
    }

    public void CloseHandle(nint handle)
    {
        Win32Common.CloseHandle(handle);
    }

    public void Info(string message)
    {
        ServiceLog.Info(message);
    }

    public void Warn(string message)
    {
        ServiceLog.Warn(message);
    }

    public void Error(string message)
    {
        ServiceLog.Error(message);
    }

    public bool TryLaunch(nint token, string exePath, string arguments,
        out nint hProcess, out uint pid, out int error)
    {
        hProcess = 0;
        pid = 0;
        error = 0;

        if (!Win32Common.CreateEnvironmentBlock(out var environment, token, false))
        {
            error = Marshal.GetLastWin32Error();
            ServiceLog.Warn(
                $"CreateEnvironmentBlock failed (error {error}); refusing to launch an "
                + "interactive process with the SYSTEM service environment.");
            return false;
        }

        var desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
        try
        {
            var startupInfo = new NativeMethods.StartupInfoW
            {
                cb = (uint)Marshal.SizeOf<NativeMethods.StartupInfoW>(),
                lpDesktop = desktop
            };
            var commandLine = string.IsNullOrEmpty(arguments) ? $"\"{exePath}\"" : $"\"{exePath}\" {arguments}";
            if (!NativeMethods.CreateProcessAsUserW(token, exePath, commandLine, 0, 0, false,
                    NativeMethods.CreateUnicodeEnvironment, environment, Path.GetDirectoryName(exePath),
                    ref startupInfo, out var processInfo))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            Win32Common.CloseHandle(processInfo.hThread);
            hProcess = processInfo.hProcess;
            pid = processInfo.dwProcessId;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(desktop);
            if (environment != 0)
            {
                Win32Common.DestroyEnvironmentBlock(environment);
            }
        }
    }

    public bool IsSessionActive(uint sessionId)
    {
        if (!NativeMethods.WTSEnumerateSessionsW(0, 0, 1, out var pSessions, out var count))
        {
            return false;
        }

        try
        {
            var size = Marshal.SizeOf<NativeMethods.WtsSessionInfoW>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<NativeMethods.WtsSessionInfoW>(pSessions + i * size);
                if (info.SessionId == sessionId)
                {
                    return info.State == NativeMethods.WtsActive;
                }
            }

            return false;
        }
        finally
        {
            Win32Common.WTSFreeMemory(pSessions);
        }
    }

    public bool IsDesktopShellInSession(nint userToken, string executable)
    {
        // A service cannot inspect another session's desktop windows. Ask the fixed-purpose,
        // pre-UI probe as the unlinked interactive user instead of mistaking a folder PID for a shell.
        if (!File.Exists(executable)
            || !string.Equals(Path.GetFileName(executable), "WSGM.exe", StringComparison.OrdinalIgnoreCase)
            || !TryLaunch(userToken, executable, "--desktop-shell-probe", out var process, out _, out _))
        {
            return false;
        }

        try
        {
            if (Win32Common.WaitForSingleObject(process, 2000) != WaitObject0)
            {
                // Only the owned, fixed-purpose probe is terminated, never the resident runtime.
                _ = NativeMethods.TerminateProcess(process, 1);
                _ = Win32Common.WaitForSingleObject(process, 1000);
                return false;
            }

            return NativeMethods.GetExitCodeProcess(process, out var code) && code == 0;
        }
        finally
        {
            Win32Common.CloseHandle(process);
        }
    }

    public string GetSessionUser(uint sessionId)
    {
        var domain = QuerySessionString(sessionId, NativeMethods.WtsInfoClassDomainName);
        var user = QuerySessionString(sessionId, NativeMethods.WtsInfoClassUserName);
        return string.IsNullOrEmpty(user) ? "unknown user" : $"{domain}\\{user}";
    }

    public IEnumerable<(uint SessionId, TimeSpan LogonAge)> ActiveSessions()
    {
        if (!NativeMethods.WTSEnumerateSessionsW(0, 0, 1, out var pSessions, out var count))
        {
            ServiceLog.Warn($"Startup catch-up: WTSEnumerateSessionsW failed (error {Marshal.GetLastWin32Error()}).");
            yield break;
        }

        try
        {
            var size = Marshal.SizeOf<NativeMethods.WtsSessionInfoW>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<NativeMethods.WtsSessionInfoW>(pSessions + i * size);
                if (info.State != NativeMethods.WtsActive)
                {
                    continue;
                }

                var logonAge = GetLogonAge(info.SessionId);
                if (logonAge is null)
                {
                    continue;
                }

                yield return (info.SessionId, logonAge.Value);
            }
        }
        finally
        {
            Win32Common.WTSFreeMemory(pSessions);
        }
    }

    public void StartWatchdog(Action watch, string name)
    {
        new Thread(() => watch()) { IsBackground = true, Name = name }.Start();
    }

    public void WaitForAnchor(TimeSpan grace)
    {
        Thread.Sleep(grace);
    }

    private static string? GetUserProfileDirectory(nint token)
    {
        uint size = 0;
        _ = NativeMethods.GetUserProfileDirectoryW(token, null, ref size);
        if (size == 0)
        {
            return null;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var buffer = new char[size];
            var available = size;
            if (NativeMethods.GetUserProfileDirectoryW(token, buffer, ref available))
            {
                var terminator = Array.IndexOf(buffer, '\0');
                return new string(buffer, 0, terminator < 0 ? buffer.Length : terminator);
            }

            if (available <= size)
            {
                return null;
            }

            size = available;
        }

        return null;
    }

    private static TimeSpan? GetLogonAge(uint sessionId)
    {
        if (!Win32Common.WTSQuerySessionInformationW(0, sessionId,
                NativeMethods.WtsInfoClassSessionInfo, out var buffer, out var bytes))
        {
            return null;
        }

        return DecodeLogonAge(buffer, bytes, Win32Common.WTSFreeMemory, DateTime.UtcNow);
    }

    internal static TimeSpan? DecodeLogonAge(nint buffer, uint bytes, Action<nint> freeBuffer, DateTime utcNow)
    {
        try
        {
            if (bytes < Marshal.SizeOf<NativeMethods.WtsInfoW>())
            {
                return null;
            }

            var info = Marshal.PtrToStructure<NativeMethods.WtsInfoW>(buffer);
            if (info.LogonTime == 0)
            {
                return null;
            }

            var logonUtc = DateTime.FromFileTimeUtc(info.LogonTime);
            var age = utcNow - logonUtc;
            return age < TimeSpan.Zero ? TimeSpan.Zero : age;
        }
        catch
        {
            return null;
        }
        finally
        {
            freeBuffer(buffer);
        }
    }

    private static string QuerySessionString(uint sessionId, int infoClass)
    {
        if (!Win32Common.WTSQuerySessionInformationW(0, sessionId, infoClass, out var buffer, out _))
        {
            return "";
        }

        try
        {
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            Win32Common.WTSFreeMemory(buffer);
        }
    }
}
