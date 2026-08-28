using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>One contained DeviceHost process and the kill-on-close job that owns it.</summary>
internal sealed class DeviceHostProcess : IDisposable
{
    private const long MemoryLimitBytes = 512L * 1024 * 1024;
    private const uint CpuRateHundredths = 5000;
    private nint _jobHandle;
    private bool _disposed;

    private DeviceHostProcess(Process process, nint jobHandle)
    {
        Process = process;
        _jobHandle = jobHandle;
    }

    public Process Process { get; }

    public void Terminate(uint exitCode)
    {
        if (_jobHandle != 0 && !Process.HasExited)
        {
            NativeDeviceHostProcess.TerminateJob(_jobHandle, exitCode);
        }
    }

    public static DeviceHostProcess Start(
        DevicePackageCandidate candidate,
        string pipeName,
        byte[] nonce,
        uint sessionId,
        long hostGeneration,
        string stateRingName,
        string stateEventName)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        string hostDirectory = Path.Combine(AppContext.BaseDirectory, "DeviceHost");
        string executable = Path.Combine(hostDirectory, "WSGM.DeviceHost.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                "The isolated DeviceHost runtime is not installed.",
                executable);
        }

        string arguments = BuildArguments(
            candidate,
            pipeName,
            nonce,
            sessionId,
            hostGeneration,
            stateRingName,
            stateEventName);
        Process process = candidate.TrustTier is DevicePluginTrustTier.WsgmReviewed
            ? StartInherited(executable, arguments, hostDirectory)
            : StartAsShellUser(executable, arguments, hostDirectory, sessionId);
        int jobError = NativeDeviceHostProcess.CreateContainedJob(
            process.Handle,
            (nuint)MemoryLimitBytes,
            CpuRateHundredths,
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
        if (_jobHandle != 0)
        {
            if (!Process.HasExited)
            {
                Terminate(1);
            }

            NativeDeviceHostProcess.CloseJob(_jobHandle);
            _jobHandle = 0;
        }

        Process.Dispose();
    }

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

    private static Process StartAsShellUser(
        string executable,
        string arguments,
        string hostDirectory,
        uint sessionId)
    {
        using Process shell = Process.GetProcessesByName("explorer")
            .FirstOrDefault(process => process.SessionId == sessionId)
            ?? throw new InvalidOperationException(
                "No Explorer process exists in the current interactive session for de-elevation.");
        string commandLine = $"{Quote(executable)} {arguments}";
        string environment = BuildEnvironmentBlock(hostDirectory);
        int error = NativeDeviceHostProcess.StartAsShellUser(
            shell.Handle,
            executable,
            commandLine,
            hostDirectory,
            environment,
            out uint processId);
        if (error != 0)
        {
            throw new InvalidOperationException(
                $"De-elevated DeviceHost launch failed with Win32 error {error}.");
        }

        return Process.GetProcessById((int)processId);
    }

    private static string BuildArguments(
        DevicePackageCandidate candidate,
        string pipeName,
        byte[] nonce,
        uint sessionId,
        long hostGeneration,
        string stateRingName,
        string stateEventName)
    {
        string packageId = candidate.Manifest?.Id
            ?? throw new InvalidOperationException("Selected package has no manifest.");
        return string.Join(' ',
            "--package", Quote(candidate.PackagePath),
            "--package-id", Quote(packageId),
            "--pipe", Quote(pipeName),
            "--nonce", Quote(Convert.ToBase64String(nonce)),
            "--session", sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--host-generation", hostGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--trust-tier", Quote(candidate.TrustTier.ToString()),
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
