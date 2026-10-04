using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace WSGM.Core;

/// <summary>Owns serialized reads and explicit writer transactions over one user's configuration.</summary>
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

    /// <summary>Reads under the same mutex that protects multi-step writer transactions.</summary>
    /// <returns>A classified result, with no defaults substituted for failure.</returns>
    public ConfigReadResult Read()
    {
        try
        {
            if (Volatile.Read(ref _writerThread) == Environment.CurrentManagedThreadId)
            {
                throw new ConfigUnavailableException("Use the active transaction's read result.");
            }

            using var held = Acquire();
            return ReadHeld();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ConfigReadResult(ConfigReadOutcome.Unreadable, null, ex);
        }
    }

    /// <summary>Starts the single writer scope; its read must be Loaded or Absent.</summary>
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
            var read = ReadHeld();
            read.RequireConfig();
            Volatile.Write(ref _writerThread, Environment.CurrentManagedThreadId);
            return new ConfigTransaction(this, held, read);
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }

    /// <summary>Applies fields to the fresh document and saves only when the caller reports a change.</summary>
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

    private ConfigReadResult ReadHeld()
    {
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

    private MutexLease Acquire()
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

            return new MutexLease(mutex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            mutex?.Dispose();
            throw new ConfigUnavailableException("The configuration mutex is unavailable.", ex);
        }
    }

    private void WriteHeld(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(Context.Root);
            AtomicFile.WriteText(ConfigPath, JsonSerializer.Serialize(config, ConfigJsonContext.Tolerant.AppConfig),
                durable: true, static (temp, ex) => Log.Warn($"Config temp cleanup failed for '{temp}': {ex.Message}"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new ConfigUnavailableException("Configuration could not be published.", ex);
        }
    }

    internal sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (Exception ex)
            {
                Log.Warn($"Config mutex release failed: {ex.Message}");
            }
            finally
            {
                mutex.Dispose();
            }
        }
    }

    /// <summary>One thread-owned writer; file promotion and boot projection may share its scope.</summary>
    public sealed class ConfigTransaction : IDisposable
    {
        private readonly ConfigStore _store;
        private readonly MutexLease _held;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private bool _disposed;

        internal ConfigTransaction(ConfigStore store, MutexLease held, ConfigReadResult read)
        {
            _store = store;
            _held = held;
            Read = read;
        }

        /// <summary>The validated read, updated to refer to a saved replacement.</summary>
        public ConfigReadResult Read { get; private set; }

        /// <summary>The fresh configuration owned by this writer.</summary>
        public AppConfig Config => Read.RequireConfig();

        /// <summary>Publishes configuration durably while retaining the writer scope.</summary>
        /// <param name="replacement">A merged replacement, or null to save the owned document.</param>
        public void Save(AppConfig? replacement = null)
        {
            EnsureOwner();
            if (replacement is not null)
            {
                Read = Read with { Config = replacement };
            }

            _store.WriteHeld(Config);
        }

        /// <summary>Releases the writer without implicitly saving.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            EnsureOwner();
            _disposed = true;
            Volatile.Write(ref _store._writerThread, 0);
            _held.Dispose();
        }

        private void EnsureOwner()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread != Environment.CurrentManagedThreadId)
            {
                throw new ConfigUnavailableException("The writer must stay on its acquiring thread.");
            }
        }
    }
}
