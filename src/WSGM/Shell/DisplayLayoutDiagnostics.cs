using System;
using System.Diagnostics;
using System.Text.Json;
using WindowsDeviceControl;

namespace WSGM.Shell;

/// <summary>Keeps the requested layout, native result and readback together in normal diagnostics.</summary>
internal static class DisplayLayoutDiagnostics
{
    internal static DisplayLayoutResult Apply(DisplayLayout layout,
        Func<DisplayLayout, DisplayLayoutResult> apply, Func<DisplayArrangement> observe,
        Action<string> info, Action<string> warn)
    {
        var operation = Guid.NewGuid().ToString("N");
        var prefix = $"Display layout {operation}";
        info($"{prefix} requested: {JsonSerializer.Serialize(layout)}");
        var elapsed = Stopwatch.StartNew();
        DisplayLayoutResult result;
        try
        {
            result = apply(layout);
        }
        catch (Exception ex)
        {
            warn($"{prefix} threw after {elapsed.ElapsedMilliseconds} ms: {ex}");
            throw;
        }

        var outcome = $"{prefix} result after {elapsed.ElapsedMilliseconds} ms: "
                      + $"outcome={result.Outcome}, nativeStatus={result.NativeStatus}, "
                      + $"rollbackAttempted={result.RollbackAttempted}, rollbackSucceeded={result.RollbackSucceeded}, "
                      + $"detail={result.Detail}, warnings={JsonSerializer.Serialize(result.Warnings)}";
        if (result is { Applied: true, Warnings.Count: 0 })
        {
            info(outcome);
        }
        else
        {
            warn(outcome);
        }

        try
        {
            info($"{prefix} readback: {JsonSerializer.Serialize(observe())}");
        }
        catch (Exception ex)
        {
            warn($"{prefix} readback unavailable: {ex.Message}");
        }

        return result;
    }
}
