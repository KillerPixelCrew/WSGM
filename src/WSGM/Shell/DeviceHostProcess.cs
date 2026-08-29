using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>One contained DeviceHost process and the kill-on-close job that owns it.</summary>
internal sealed class DeviceHostProcess : IDisposable
{
    private nint _jobHandle;
    private bool _disposed;

    private DeviceHostProcess(Process process, nint jobHandle)
    {
        Process = process;
        _jobHandle = jobHandle;
    }

    public Process Process { get; }

    internal static bool? IsAnyRunning()
    {
        try
        {
            Process[] processes = System.Diagnostics.Process.GetProcessesByName("WSGM.DeviceHost");
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            Log.Warn($"DeviceHost process state is unknown: {ex.Message}");
            return null;
        }
    }

    public void Terminate(uint exitCode)
    {
        if (_jobHandle != 0 && !Process.HasExited)
        {
            NativeDeviceHostProcess.TerminateJob(_jobHandle, exitCode);
        }
    }

    public static DeviceHostProcess Start(
        InstalledDevicePackage package,
        string pipeName,
        byte[] nonce,
        uint sessionId,
        long cycleGeneration,
        string stateRingName,
        string stateEventName)
    {
        ArgumentNullException.ThrowIfNull(package);
        string hostDirectory = DeviceInstallationPaths.DeviceHostRoot;
        string executable = Path.Combine(hostDirectory, "WSGM.DeviceHost.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                "The isolated DeviceHost runtime is not installed.",
                executable);
        }

        string arguments = BuildArguments(
            package,
            pipeName,
            nonce,
            sessionId,
            cycleGeneration,
            stateRingName,
            stateEventName);
        Process process = StartInherited(executable, arguments, hostDirectory);
        int jobError = NativeDeviceHostProcess.CreateKillOnCloseJob(
            process.Handle,
            out nint jobHandle);
        if (jobError != 0)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            process.Dispose();
            throw new InvalidOperationException(
                $"DeviceHost job containment failed with Win32 error {jobError}.");
        }

        return new DeviceHostProcess(process, jobHandle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        nint jobHandle = Interlocked.Exchange(ref _jobHandle, 0);
        try
        {
            if (jobHandle != 0)
            {
                try
                {
                    if (!Process.HasExited)
                    {
                        _ = NativeDeviceHostProcess.TerminateJob(jobHandle, 1);
                    }
                }
                finally
                {
                    // Kill-on-close is the last containment boundary even when querying or
                    // terminating the child reports a process-state failure.
                    NativeDeviceHostProcess.CloseJob(jobHandle);
                }
            }
        }
        finally
        {
            Process.Dispose();
        }
    }

    /// <summary>Snapshots global DeviceHost process state away from the Avalonia UI thread.</summary>
    internal static Task<bool?> IsAnyRunningAsync(
        CancellationToken cancellationToken = default,
        Func<bool?>? inspect = null) =>
        Task.Run(inspect ?? IsAnyRunning, cancellationToken);

    private static Process StartInherited(
        string executable,
        string arguments,
        string hostDirectory)
    {
        ProcessStartInfo start = new(executable, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = hostDirectory,
        };
        SanitizeEnvironment(start.Environment, hostDirectory);
        return Process.Start(start)
            ?? throw new InvalidOperationException("DeviceHost process did not start.");
    }

    private static string BuildArguments(
        InstalledDevicePackage package,
        string pipeName,
        byte[] nonce,
        uint sessionId,
        long cycleGeneration,
        string stateRingName,
        string stateEventName)
    {
        string packageId = package.Manifest?.Id
            ?? throw new InvalidOperationException("Installed package has no manifest.");
        return string.Join(' ',
            "--package", Quote(package.PackagePath),
            "--package-id", Quote(packageId),
            "--pipe", Quote(pipeName),
            "--nonce", Quote(Convert.ToBase64String(nonce)),
            "--session", sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--generation", cycleGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--state-ring", Quote(stateRingName),
            "--state-event", Quote(stateEventName));
    }

    private static string BuildEnvironmentBlock(string hostDirectory)
    {
        Dictionary<string, string> environment = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[]
        {
            "ALLUSERSPROFILE",
            "APPDATA",
            "LOCALAPPDATA",
            "ProgramData",
            "ProgramFiles",
            "ProgramFiles(x86)",
            "SystemDrive",
            "SystemRoot",
            "TEMP",
            "TMP",
            "USERPROFILE",
            "windir",
        })
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                environment[name] = value;
            }
        }

        environment["PATH"] = string.Join(Path.PathSeparator,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"),
            hostDirectory);
        StringBuilder block = new();
        foreach ((string key, string value) in environment.OrderBy(pair => pair.Key,
            StringComparer.OrdinalIgnoreCase))
        {
            block.Append(key).Append('=').Append(value).Append('\0');
        }

        block.Append('\0');
        return block.ToString();
    }

    private static void SanitizeEnvironment(
        IDictionary<string, string?> environment,
        string hostDirectory)
    {
        string block = BuildEnvironmentBlock(hostDirectory);
        environment.Clear();
        foreach (string entry in block.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = entry.IndexOf('=');
            if (separator > 0)
            {
                environment[entry[..separator]] = entry[(separator + 1)..];
            }
        }
    }

    private static string Quote(string argument) => SelfElevation.Quote(argument);
}
