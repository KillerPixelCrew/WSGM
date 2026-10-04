using System;
using System.IO;
using System.Text;

namespace WSGM.PackagedLaunch;

/// <summary>Finds a module using complete API-sized lists and paths.</summary>
public static class CompleteModuleInspection
{
    /// <summary>Finds one filename without clipping either the module list or its paths.</summary>
    /// <param name="fileName">The filename to find.</param>
    /// <param name="query">Returns module handles and the API's required byte count.</param>
    /// <param name="path">Reads a module path, returning characters copied.</param>
    /// <returns>The matched handle, or zero when unavailable.</returns>
    public static nint Find(string fileName, Func<nint[], (bool Success, uint RequiredBytes)> query,
        Func<nint, StringBuilder, uint> path)
    {
        var handles = new nint[64];
        uint required;
        while (true)
        {
            var result = query(handles);
            if (!result.Success || result.RequiredBytes % nint.Size != 0)
            {
                return 0;
            }

            required = result.RequiredBytes;
            var count = checked((int)(required / nint.Size));
            if (count <= handles.Length)
            {
                break;
            }

            handles = new nint[count];
        }

        for (var index = 0; index < required / nint.Size; index++)
        {
            var buffer = new StringBuilder(260);
            uint length;
            // A null-terminated truncated result can consume capacity minus one characters.
            while ((length = path(handles[index], buffer)) >= buffer.Capacity - 1)
            {
                buffer = new StringBuilder(checked(buffer.Capacity * 2));
            }

            if (length != 0 && string.Equals(Path.GetFileName(buffer.ToString()), fileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return handles[index];
            }
        }

        return 0;
    }
}
