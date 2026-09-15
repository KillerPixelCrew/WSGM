using System;
using System.IO;

namespace WSGM.Core;

/// <summary>Best-effort removal of files nothing depends on any more.</summary>
internal static class FileCleanup
{
    /// <summary>Deletes a file when it is there, never throwing.</summary>
    /// <param name="path">The file to remove.</param>
    /// <param name="failed">Told why an existing file could not be removed.</param>
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
