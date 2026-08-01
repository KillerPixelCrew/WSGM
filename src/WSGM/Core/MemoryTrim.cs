using System;
using System.Runtime;

namespace WSGM.Core;

/// <summary>Returns idle memory to the OS. A resident shell is judged by its
/// Task Manager number while it sits invisible behind a game — after the UI
/// moments (boot, overlay) pass, compact the heap and empty the working set.
/// Trimmed pages come back via cheap soft faults on the next overlay open.</summary>
public static class MemoryTrim
{
    /// <summary>Never throws; logs before/after so the effect is visible in a
    /// pasted device log. Safe on any thread.</summary>
    public static void TrimBestEffort(string reason)
    {
        try
        {
            var before = Environment.WorkingSet;
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            Interop.NativeMethods.EmptyWorkingSet(Interop.NativeMethods.GetCurrentProcess());
            var after = Environment.WorkingSet;
            Log.Info($"Memory trimmed ({reason}): working set {before / (1024 * 1024)} -> {after / (1024 * 1024)} MB.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Memory trim failed ({reason}): {ex.Message}");
        }
    }
}
