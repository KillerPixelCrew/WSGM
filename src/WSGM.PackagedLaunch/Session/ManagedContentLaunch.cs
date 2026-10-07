using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using WSGM.Core;
using WSGM.Launch;

namespace WSGM.PackagedLaunch;

/// <summary>The authoritative launch boundary for managed ROM and portable content.</summary>
internal static class ManagedContentLaunch
{
    internal static int Run(string id)
    {
        var userRoot = UserDataContext.ForCurrentUser().Root;
        while (true)
        {
            try
            {
                using GameSessionJob job = new(reportCompletion: true);
                if (!job.Available)
                {
                    return Refuse("WSGM could not track this game's session, so it was not started. Try again.", false);
                }

                ManagedContentCheck check;
                Process? child = null;
                using (var admission = EmulatorStorage.TryAcquireGate(userRoot, TimeSpan.Zero))
                {
                    if (admission is null)
                    {
                        return Refuse("An emulator operation is in progress. Wait for it to finish, then try again.",
                            false);
                    }

                    check = ManagedContentStorage.Check(ManagedContentStorage.Read(userRoot, id),
                        EmulatorStorage.ReadStore(userRoot));
                    if (check.Available && check.Content!.LaunchKind == ManagedLaunchKind.Direct)
                    {
                        var start = StartInfo(check);
                        ContainedProcessStart.UseCallerIntegrity(start);
                        child = ContainedProcessStart.Start(start, job.ContainSuspended);
                    }
                }

                if (child is null)
                {
                    var record = check.Content!;
                    var detail = check.Available
                        ? "This title uses a native launcher route. Rescan its source in Game Library."
                        : check.Detail;
                    if (Refuse($"{record.Name}\n\n{detail}\n\nExpected location: {record.Location}", true) == 1)
                    {
                        continue;
                    }

                    return Program.ExitActivationFailed;
                }

                using (child)
                {
                    PackagedLaunchLog.Info(
                        $"Managed content started (pid {child.Id}; source {check.Content!.SourceKind}).");
                    child.WaitForExit();
                    job.WaitForEmpty();
                    return child.ExitCode;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
                                           or UnauthorizedAccessException or Win32Exception or ArgumentException
                                           or JsonException)
            {
                PackagedLaunchLog.Error($"Managed content launch refused: {ex.Message}");
                if (Refuse("This title could not be started. " + ex.Message
                                                               + "\n\nConnect its required storage or check its emulator in Game Library.",
                        true) != 1)
                {
                    return Program.ExitActivationFailed;
                }
            }
        }
    }

    private static ProcessStartInfo StartInfo(ManagedContentCheck check)
    {
        var record = check.Content!;
        ProcessStartInfo start = new(check.Launch?.Executable ?? check.ProgramPath)
        {
            UseShellExecute = false,
            WorkingDirectory = check.Launch?.WorkingDirectory ?? check.WorkingDirectoryPath
        };
        if (check.Launch is { } launch)
        {
            foreach (var argument in launch.Arguments)
            {
                start.ArgumentList.Add(argument);
            }

            foreach (var pair in launch.Environment)
            {
                start.Environment[pair.Key] = pair.Value;
            }
        }
        else
        {
            start.Arguments = record.RawArguments.Replace("{content}", WindowsCommandLine.Quote(check.Path),
                StringComparison.Ordinal);
        }

        start.Environment.Remove(SteamControllerExclusion.Variable);
        return start;
    }

    private static int Refuse(string message, bool recheck)
    {
        PackagedLaunchLog.Warn(message.Replace('\n', ' '));
        var answer = ManagedDirectNative.MessageBoxW(0,
            recheck ? message + "\n\nChoose Retry to recheck now." : message,
            "WSGM: Content unavailable", recheck ? 0x35u : 0x30u);
        return recheck && answer == 4 ? 1 : Program.ExitActivationFailed;
    }
}
