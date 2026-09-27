using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace WSGM.Core;

/// <summary>Reads a launcher's SQLite database without touching the launcher's own file.</summary>
/// <remarks>
///     <para>
///         A running launcher holds its database open and may be writing to it, so the file and its
///         <c>-wal</c> and <c>-shm</c> siblings are copied into a private temporary folder first and
///         the copy is opened read-only. Nothing is ever written next to the launcher's file, and a
///         half-written page in the original cannot corrupt anything the launcher owns.
///     </para>
///     <para>
///         Pooling is off so the copy's handle closes with the connection and the folder can be
///         deleted straight away.
///     </para>
/// </remarks>
internal static class LauncherDatabase
{
    private static readonly string[] Siblings = ["-wal", "-shm"];

    /// <summary>Runs one query against a copy of a database.</summary>
    /// <typeparam name="T">What each row becomes.</typeparam>
    /// <param name="path">The launcher's database file.</param>
    /// <param name="sql">The query.</param>
    /// <param name="map">Turns the reader's current row into a value.</param>
    /// <returns>The rows, or an empty list when the database cannot be copied, opened or queried.</returns>
    internal static IReadOnlyList<T> ReadRows<T>(string path, string sql, Func<SqliteDataReader, T> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var folder = Path.Combine(Path.GetTempPath(), "wsgm-library-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, Path.GetFileName(path));
            CopyShared(path, copy);
            foreach (var suffix in Siblings)
            {
                if (File.Exists(path + suffix))
                {
                    CopyShared(path + suffix, copy + suffix);
                }
            }

            List<T> rows = [];
            using (var connection = new SqliteConnection($"Data Source={copy};Mode=ReadOnly;Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(map(reader));
                }
            }

            return rows;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException or InvalidCastException
                                       or FormatException)
        {
            Log.Warn($"Game Library could not read {path}: {ex.Message}");
            return [];
        }
        finally
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Game Library could not remove its database copy {folder}: {ex.Message}");
            }
        }
    }

    /// <summary>Text from a column, or empty when it is null.</summary>
    /// <param name="reader">The reader on its current row.</param>
    /// <param name="ordinal">The column.</param>
    /// <returns>The value as invariant text.</returns>
    internal static string Text(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? string.Empty
            : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)
              ?? string.Empty;
    }

    /// <summary>Copies a file the launcher may still have open for writing.</summary>
    private static void CopyShared(string source, string destination)
    {
        using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }
}
