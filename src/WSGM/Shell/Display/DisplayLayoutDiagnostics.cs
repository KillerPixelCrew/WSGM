using System;
using System.Diagnostics;
using System.Text.Json;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Keeps the requested layout, the native result and the desktop observed afterwards together in normal
///     diagnostics. The observation is logged for diagnosis only; nothing is decided from it.
/// </summary>
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
                      + $"detail={DisplayText.Layout(result)}, "
                      + $"warnings={JsonSerializer.Serialize(DisplayText.Warnings(result))}";
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
