// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace WSGM.Plugin.Gpu;

/// <summary>
///     Atomic private state. A missing file is absent; a corrupt or unreadable one throws and is never replaced.
/// </summary>
internal static class DriverStateFile
{
    internal static T Read<T>(string path, T empty)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize<T>(file)
                   ?? throw new DriverFailure($"{Path.GetFileName(path)} is corrupt: it holds no record.");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return empty;
        }
        catch (JsonException error)
        {
            throw new DriverFailure($"{Path.GetFileName(path)} is corrupt: {error.Message}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new DriverFailure($"{Path.GetFileName(path)} could not be read: {error.Message}");
        }
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
