using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Owns serialized reads and explicit writer transactions over one user's configuration.</summary>
/// <remarks>
///     Reads classify absence separately from corrupt or inaccessible state. A writer always requires
///     a fresh Loaded or Absent result under the cross-process mutex; startup fallback defaults do not
///     authorize overwriting an existing unreadable file. Publication atomically replaces one file.
/// </remarks>
public sealed class ConfigStore
{
    private const int MutexTimeoutMs = 2000;
    private int _writerThread;

    /// <summary>Creates persistence over an explicit filesystem and mutex context.</summary>
    /// <param name="context">The user's data and lock identity.</param>
    public ConfigStore(UserDataContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>The filesystem and lock identity used by this store.</summary>
    public UserDataContext Context { get; }

    /// <summary>The configuration file.</summary>
    public string ConfigPath => Path.Combine(Context.Root, "config.json");

    /// <summary>
    ///     Reads under the same mutex that protects multi-step writer transactions. When a writer holds it past
    ///     the timeout, the read goes ahead without it: every write replaces the file atomically, so a reader
    ///     still sees one whole document.
    /// </summary>
    /// <returns>A classified result, with no defaults substituted for failure.</returns>
    public ConfigReadResult Read()
    {
        try
        {
            if (Volatile.Read(ref _writerThread) == Environment.CurrentManagedThreadId)
            {
                throw new ConfigUnavailableException("Use the active transaction's read result.");
            }

            ConfigMutexLease? held;
            try
            {
                held = Acquire();
            }
            catch (ConfigUnavailableException ex) when (ex.InnerException is TimeoutException)
            {
                Log.Warn("Configuration is busy; reading it without the lock.");
                held = null;
            }

            using (held)
            {
                return ReadHeld(out _);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ConfigReadResult(ConfigReadOutcome.Unreadable, null, ex);
        }
    }

    /// <summary>Starts the single writer scope; its read must be Loaded or Absent.</summary>
    /// <remarks>
    ///     Keep the scope on its acquiring thread, including Save and Dispose; do not carry it across
    ///     an await. Use the transaction's Read instead of reading through this store inside the scope.
    ///     Disposing the scope releases the mutex without implicitly saving.
    /// </remarks>
    /// <returns>A scope that owns the fresh configuration and mutex.</returns>
    /// <exception cref="ConfigUnavailableException">The lock or existing document is unavailable.</exception>
    public ConfigTransaction Transaction()
    {
        if (Volatile.Read(ref _writerThread) == Environment.CurrentManagedThreadId)
        {
            throw new ConfigUnavailableException("A configuration writer transaction cannot be nested.");
        }

        var held = Acquire();
        try
        {
            var read = ReadHeld(out var older);
            read.RequireConfig();
            Volatile.Write(ref _writerThread, Environment.CurrentManagedThreadId);
            return new ConfigTransaction(this, held, read, older);
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }

    /// <summary>Clears the writer thread when a <see cref="ConfigTransaction" /> ends.</summary>
    internal void ReleaseWriterThread()
    {
        Volatile.Write(ref _writerThread, 0);
    }

    /// <summary>Applies fields to the fresh document and saves only when the caller reports a change.</summary>
    /// <remarks>
    ///     The edit runs synchronously under the writer mutex. Even when it reports a change, an
    ///     identical serialized result does not write the file. Limit the edit to the caller's fields.
    /// </remarks>
    /// <param name="edit">Mutates only the caller's fields and returns whether to save.</param>
    /// <returns>The fresh configuration after the edit.</returns>
    public AppConfig Update(Func<AppConfig, bool> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        using var transaction = Transaction();
        var before = JsonSerializer.Serialize(transaction.Config, ConfigJsonContext.Tolerant.AppConfig);
        if (edit(transaction.Config))
        {
            var after = JsonSerializer.Serialize(transaction.Config, ConfigJsonContext.Tolerant.AppConfig);
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                transaction.Save();
            }
        }

        return transaction.Config;
    }

    /// <summary>
    ///     Applies an edit through <see cref="Update" /> on a worker thread, saving whenever it changed the
    ///     document, and answers what the edit computed.
    /// </summary>
    /// <typeparam name="T">The value the edit returns to the caller.</typeparam>
    /// <param name="edit">Mutates only the caller's fields and returns a detached value.</param>
    /// <param name="cancellationToken">Cancels the work before it starts.</param>
    /// <returns>The edit's result once the configuration update finished.</returns>
    public Task<T> UpdateAsync<T>(Func<AppConfig, T> edit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return Task.Run(() =>
        {
            T result = default!;
            Update(config =>
            {
                result = edit(config);
                return true;
            });
            return result;
        }, cancellationToken);
    }

    /// <summary>
    ///     Reads, repairs, normalizes and migrates the stored document while the mutex is held, or after its
    ///     timeout for a plain <see cref="Read" />.
    /// </summary>
    /// <param name="older">The stored bytes and schema of a file an older WSGM wrote, kept for one copy.</param>
    /// <returns>A classified result, with no defaults substituted for failure.</returns>
    private ConfigReadResult ReadHeld(out (byte[] Bytes, int Version)? older)
    {
        older = null;
        byte[] bytes;
        try
        {
            using var input = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            bytes = buffer.ToArray();
        }
        catch (FileNotFoundException)
        {
            return new ConfigReadResult(ConfigReadOutcome.Absent, new AppConfig());
        }
        catch (DirectoryNotFoundException)
        {
            return new ConfigReadResult(ConfigReadOutcome.Absent, new AppConfig());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ConfigReadResult(ConfigReadOutcome.Unreadable, null, ex);
        }

        try
        {
            using var memory = new MemoryStream(bytes);
            using var reader = new StreamReader(memory, Encoding.UTF8, true);
            var parsed = ConfigRepair.Deserialize(reader.ReadToEnd());
            var normalized = AppConfigRules.Normalize(parsed);
            foreach (var diagnostic in normalized.Diagnostics)
            {
                Log.Warn(diagnostic);
            }

            var stored = ConfigRepair.Migrate(normalized.Value);
            if (stored < AppConfig.CurrentSchemaVersion)
            {
                older = (bytes, stored);
            }

            return new ConfigReadResult(ConfigReadOutcome.Loaded, normalized.Value);
        }
        catch (JsonException ex)
        {
            PreserveCorrupt(bytes);
            return new ConfigReadResult(ConfigReadOutcome.Corrupt, null, ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ConfigReadResult(ConfigReadOutcome.Unreadable, null, ex);
        }
    }

    private void PreserveCorrupt(byte[] bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var backup = Path.Combine(Context.Root, $"config.bad.{hash}.json");
        var created = false;
        try
        {
            using var output = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough);
            created = true;
            output.Write(bytes);
            output.Flush(true);
            Log.Warn($"Corrupt configuration preserved at {backup}; the original remains for repair.");
        }
        catch (IOException) when (!created && File.Exists(backup))
        {
            // The same content was already preserved. Never prune distinct recovery evidence.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (created)
            {
                try
                {
                    File.Delete(backup);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Incomplete corrupt backup cleanup failed: {cleanup.Message}");
                }
            }

            Log.Warn($"Corrupt configuration could not be preserved: {ex.Message}");
        }
    }

    /// <summary>
    ///     Keeps the file an older WSGM wrote as <c>config.v&lt;schema&gt;.json</c> before the first write in the
    ///     current schema replaces it. One copy: an existing one is the first older file and stays. Without the
    ///     copy the write is refused, so the older file is never replaced unpreserved.
    /// </summary>
    internal void PreserveOlderSchema(byte[] bytes, int version)
    {
        var copy = Path.Combine(Context.Root, $"config.v{version}.json");
        var created = false;
        try
        {
            using var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough);
            created = true;
            output.Write(bytes);
            output.Flush(true);
            Log.Info($"Configuration schema {version} migrates to {AppConfig.CurrentSchemaVersion}; "
                     + $"the old file is kept at {copy}.");
        }
        catch (IOException) when (!created && File.Exists(copy))
        {
            // The first older file is already kept.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (created)
            {
                try
                {
                    File.Delete(copy);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Incomplete old configuration copy cleanup failed: {cleanup.Message}");
                }
            }

            throw new ConfigUnavailableException("The old configuration could not be kept before migrating it.", ex);
        }
    }

    private ConfigMutexLease Acquire()
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, Context.ConfigMutexName);
            bool owned;
            try
            {
                owned = mutex.WaitOne(MutexTimeoutMs);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            if (!owned)
            {
                throw new TimeoutException("The configuration is busy.");
            }

            return new ConfigMutexLease(mutex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            mutex?.Dispose();
            throw new ConfigUnavailableException("The configuration mutex is unavailable.", ex);
        }
    }

    /// <summary>Publishes <paramref name="config" /> while the caller holds the writer mutex.</summary>
    /// <param name="config">The configuration to write.</param>
    internal void WriteHeld(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(Context.Root);
            AtomicFile.WriteText(ConfigPath, JsonSerializer.Serialize(config, ConfigJsonContext.Tolerant.AppConfig),
                true, static (temp, ex) => Log.Warn($"Config temp cleanup failed for '{temp}': {ex.Message}"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new ConfigUnavailableException("Configuration could not be published.", ex);
        }
    }
}
