using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace WSGM.Core;

/// <summary>A launcher's database could not be read, so nothing it lists is known.</summary>
/// <param name="message">What went wrong, naming the file.</param>
/// <param name="inner">The failure underneath.</param>
/// <remarks>
///     Thrown rather than answered with no rows: a source that found nothing reports every title it
///     imported before as uninstalled, and a locked or half-copied database is not that.
/// </remarks>
public sealed class LauncherDatabaseException(string message, Exception inner) : IOException(message, inner);

/// <summary>Reads a launcher's SQLite database without touching the launcher's own file.</summary>
/// <remarks>
///     <para>
///         A running launcher holds its database open and may be writing to it, so the file and its
///         write-ahead log are copied into a private temporary folder first and the copy is opened.
///         Nothing is ever written next to the launcher's file, and a half-written page in the
///         original cannot corrupt anything the launcher owns.
///     </para>
///     <para>
///         The log is copied before the database. A checkpoint that runs between the two copies has
///         then moved pages the copied log still holds, and replaying them onto the newer database
///         writes the same pages again; copied the other way round, the checkpointed pages would be in
///         neither copy. The shared-memory index is never copied, because a stale one describes a log
///         it does not match; SQLite rebuilds it from the log. A copy that still reads as corrupt,
///         because the launcher wrote during the copy, is taken again, a bounded number of times.
///     </para>
///     <para>
///         Pooling is off so the copy's handle closes with the connection and the folder can be
///         deleted straight away.
///     </para>
/// </remarks>
internal static class LauncherDatabase
{
    /// <summary>How many copies are taken before a database that keeps reading as corrupt is given up on.</summary>
    private const int MaximumAttempts = 3;

    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    /// <summary>Runs one query against a copy of a database.</summary>
    /// <typeparam name="T">What each row becomes.</typeparam>
    /// <param name="path">The launcher's database file.</param>
    /// <param name="sql">The query.</param>
    /// <param name="map">Turns the reader's current row into a value.</param>
    /// <returns>The rows.</returns>
    /// <exception cref="LauncherDatabaseException">The database could not be copied, opened or queried.</exception>
    internal static IReadOnlyList<T> ReadRows<T>(string path, string sql, Func<SqliteDataReader, T> map)
    {
        return Read(path, connection => Query(connection, sql, map));
    }

    /// <summary>Opens one copy of a database and reads it however the caller needs.</summary>
    /// <typeparam name="T">What the read produces.</typeparam>
    /// <param name="path">The launcher's database file.</param>
    /// <param name="read">Reads the open copy; may run several queries.</param>
    /// <returns>What <paramref name="read" /> returned.</returns>
    /// <exception cref="LauncherDatabaseException">The database could not be copied, opened or read.</exception>
    /// <remarks>
    ///     One copy per call, however many queries <paramref name="read" /> runs: a caller that tries a
    ///     query per schema version pays for one copy of the file, not one per attempt.
    /// </remarks>
    internal static T Read<T>(string path, Func<SqliteConnection, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        for (var attempt = 1;; attempt++)
        {
            var folder = Path.Combine(Path.GetTempPath(), "wsgm-library-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(folder);
                var copy = Path.Combine(folder, Path.GetFileName(path));
                if (File.Exists(path + "-wal"))
                {
                    CopyShared(path + "-wal", copy + "-wal");
                }

                CopyShared(path, copy);
                SqliteConnectionStringBuilder builder = new()
                {
                    DataSource = copy,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false
                };
                using SqliteConnection connection = new(builder.ToString());
                connection.Open();
                return read(connection);
            }
            catch (SqliteException ex) when (attempt < MaximumAttempts
                                             && ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
            {
                Log.Debug($"Game Library read a torn copy of {path}; copying it again.");
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException
                                           or InvalidOperationException or InvalidCastException
                                           or FormatException)
            {
                Log.Warn($"Game Library could not read {path}: {ex.Message}");
                throw new LauncherDatabaseException(
                    $"{Path.GetFileName(path)} could not be read: {ex.Message}", ex);
            }
            finally
            {
                Delete(folder);
            }
        }
    }

    /// <summary>Runs one query on an open copy.</summary>
    /// <typeparam name="T">What each row becomes.</typeparam>
    /// <param name="connection">The open copy.</param>
    /// <param name="sql">The query.</param>
    /// <param name="map">Turns the reader's current row into a value.</param>
    /// <returns>The rows.</returns>
    internal static IReadOnlyList<T> Query<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> map)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(map);
        List<T> rows = [];
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return rows;
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

    private static void Delete(string folder)
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
