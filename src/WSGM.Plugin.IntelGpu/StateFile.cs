using System.Text.Json;

namespace WSGM.Plugin.IntelGpu;

/// <summary>The package's JSON records in its state directory, written atomically.</summary>
internal static class StateFile
{
    /// <summary>Reads a record.</summary>
    /// <typeparam name="T">The record type.</typeparam>
    /// <param name="path">The file, or null when the plugin has no state directory.</param>
    /// <param name="log">Receives a read failure.</param>
    /// <param name="scope">The trace scope.</param>
    /// <param name="what">What the record is, for the trace line.</param>
    /// <returns>The record, or null when it is absent or unreadable.</returns>
    public static T? TryLoad<T>(string? path, IntelLog log, string scope, string what)
        where T : class
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            log.Warn(scope, $"The {what} could not be read and is ignored: {IntelLog.Describe(error)}");
            return null;
        }
    }

    /// <summary>Writes a record through a temporary file and a move, so a crash never leaves half of it.</summary>
    /// <typeparam name="T">The record type.</typeparam>
    /// <param name="path">The file, or null when the plugin has no state directory.</param>
    /// <param name="value">The record.</param>
    /// <param name="log">Receives a write failure.</param>
    /// <param name="scope">The trace scope.</param>
    /// <param name="what">What the record is, for the trace line.</param>
    public static void Save<T>(string? path, T value, IntelLog log, string scope, string what)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log.Error(scope, $"The {what} could not be saved: {IntelLog.Describe(error)}");
        }
    }
}
