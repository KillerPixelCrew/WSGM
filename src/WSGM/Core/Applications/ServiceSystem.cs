using Microsoft.Win32;

namespace WSGM.Core;

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
