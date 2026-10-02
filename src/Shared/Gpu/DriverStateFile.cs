// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace WSGM.Plugin.Gpu;

/// <summary>Atomic private state. An unreadable journal is never replaced with an empty one.</summary>
internal static class DriverStateFile
{
    internal static T Read<T>(string path, T empty)
    {
        if (!File.Exists(path))
        {
            return empty;
        }

        if (new FileInfo(path).Length > 4 * 1024 * 1024)
        {
            throw new DriverFailure("The GPU ownership journal exceeds its size bound.");
        }

        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path))
               ?? throw new DriverFailure("The GPU ownership journal is unreadable.");
    }

    internal static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(file, value);
                file.Flush(true);
            }

            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
