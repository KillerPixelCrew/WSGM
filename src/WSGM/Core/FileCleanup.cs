using System;
using System.IO;

namespace WSGM.Core;

/// <summary>Best-effort removal of files nothing depends on any more.</summary>
internal static class FileCleanup
{
    /// <summary>Attempts file deletion and reports failure through an optional callback.</summary>
    /// <param name="path">The file to remove.</param>
    /// <param name="failed">Optional synchronous failure observer; exceptions it throws propagate.</param>
    internal static void TryDelete(string path, Action<Exception>? failed = null)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            failed?.Invoke(ex);
        }
    }
}
