using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>The Windows services the manager takeover reads and changes.</summary>
public interface IServiceSystem
{
    /// <summary>Reads a service's start type (2 automatic, 3 manual, 4 disabled), or null when it does not exist.</summary>
    /// <param name="service">Service registry name, not its display name.</param>
    /// <param name="delayed">Receives the delayed-auto-start flag, false when absent.</param>
    /// <returns>The raw start value, or null when it cannot be found; access failures may propagate.</returns>
    int? ReadStart(string service, out bool delayed);

    /// <summary>Sets a service's start type.</summary>
    /// <param name="service">Service registry name.</param>
    /// <param name="start">2 for automatic, 3 for manual, or 4 for disabled.</param>
    /// <param name="delayed">Whether an automatic start should be delayed; ignored for other start types.</param>
    /// <returns>Whether the service manager accepted the configuration command.</returns>
    bool SetStart(string service, int start, bool delayed);

    /// <summary>Asks a service to stop.</summary>
    /// <param name="service">Service registry name.</param>
    /// <returns>Whether the stop command succeeded; this does not wait for the service to become stopped.</returns>
    bool Stop(string service);

    /// <summary>Asks a service to start.</summary>
    /// <param name="service">Service registry name.</param>
    /// <returns>Whether the start command succeeded; this does not prove the service is ready.</returns>
    bool Start(string service);
}

/// <summary>The live services: start types from the registry, changes through <c>sc.exe</c> so the service manager knows.</summary>
public sealed class ServiceSystem : IServiceSystem
{
    /// <inheritdoc />
    public int? ReadStart(string service, out bool delayed)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
        delayed = key?.GetValue("DelayedAutostart") is int delay && delay != 0;
        return key?.GetValue("Start") is int start ? start : null;
    }

    /// <inheritdoc />
    public bool SetStart(string service, int start, bool delayed)
    {
        var mode = start switch
        {
            2 when delayed => "delayed-auto",
            2 => "auto",
            3 => "demand",
            4 => "disabled",
            _ => null
        };
        return mode is not null &&
               ConsoleTool.RunAsync(ConsoleTool.System32("sc.exe"), $"config \"{service}\" start= {mode}")
                   .GetAwaiter().GetResult().Outcome == ConsoleToolRunOutcome.Succeeded;
    }

    /// <inheritdoc />
    public bool Stop(string service)
    {
        var result = ConsoleTool.RunAsync(ConsoleTool.System32("sc.exe"), $"stop \"{service}\"")
            .GetAwaiter().GetResult();
        return result.Outcome == ConsoleToolRunOutcome.Succeeded || result.ExitCode == 1062;
    }

    /// <inheritdoc />
    public bool Start(string service)
    {
        return ConsoleTool.RunAsync(ConsoleTool.System32("sc.exe"), $"start \"{service}\"")
            .GetAwaiter().GetResult().Outcome == ConsoleToolRunOutcome.Succeeded;
    }
}
