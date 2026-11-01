using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>The class translations themes are written against: the saved table, its fetch and its retry.</summary>
/// <remarks>
///     A fetched table is kept in the themes folder, so the next start reads it before the store answers. The
///     owner applies a table to its loader under its own lock and publishes the change.
/// </remarks>
internal sealed class ThemeTranslations
{
    /// <summary>How long a failed fetch waits before the next try, as CSS Loader waits.</summary>
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(60);

    private readonly Action<ThemeClassMappings> _apply;
    private readonly Func<bool> _beta;
    private readonly ThemeStoreClient _client;
    private readonly string _root;
    private readonly Lock _sync = new();
    private int _count;
    private DateTimeOffset? _fetched;

    /// <summary>Creates the translations for one themes folder.</summary>
    /// <param name="client">The store the tables come from.</param>
    /// <param name="root">The themes folder the fetched table is kept in.</param>
    /// <param name="beta">Whether the beta table is wanted now.</param>
    /// <param name="apply">Gives a fetched table to the loader and publishes it.</param>
    internal ThemeTranslations(ThemeStoreClient client, string root, Func<bool> beta,
        Action<ThemeClassMappings> apply)
    {
        _client = client;
        _root = root;
        _beta = beta;
        _apply = apply;
    }

    /// <summary>How many class translations the current table holds.</summary>
    internal int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    /// <summary>When the current table was fetched, or null before any table was read.</summary>
    internal DateTimeOffset? Fetched
    {
        get
        {
            lock (_sync)
            {
                return _fetched;
            }
        }
    }

    /// <summary>Reads the table kept by the last fetch.</summary>
    /// <returns>The saved table for the caller to apply, or null when none is kept or it cannot be read.</returns>
    internal ThemeClassMappings? LoadFile()
    {
        var path = Path.Combine(_root, ThemePaths.TranslationsFileName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var mappings = ThemeClassMappings.Parse(File.ReadAllText(path));
            var fetched = File.GetLastWriteTimeUtc(path);
            lock (_sync)
            {
                _count = mappings.Count;
                _fetched = fetched;
            }

            return mappings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Themes: the saved class translations could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>Fetches every minute until one fetch succeeds, as CSS Loader retries.</summary>
    /// <param name="cancellationToken">Ends the retries.</param>
    /// <returns>A task that completes after the first successful fetch or on cancellation.</returns>
    /// <remarks>A later branch change asks again on its own through <see cref="FetchOnceAsync" />.</remarks>
    internal async Task RunUntilFetchedAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await FetchOnceAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                await Task.Delay(Retry, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Fetches the wanted table once, keeps it and applies it.</summary>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>Whether a table was fetched and applied.</returns>
    internal async Task<bool> FetchOnceAsync(CancellationToken cancellationToken)
    {
        var beta = _beta();
        string text;
        try
        {
            text = await _client.TranslationsAsync(beta, cancellationToken).ConfigureAwait(false);
        }
        catch (ThemeStoreException ex)
        {
            Log.Change("themes.translations", $"Themes: class translations not fetched: {ex.Message}", LogLevel.Warn);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        ThemeClassMappings mappings;
        try
        {
            mappings = ThemeClassMappings.Parse(text);
        }
        catch (JsonException ex)
        {
            Log.Warn($"Themes: the class translations could not be read: {ex.Message}");
            return false;
        }

        try
        {
            Directory.CreateDirectory(_root);
            AtomicFile.WriteText(Path.Combine(_root, ThemePaths.TranslationsFileName), text, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Themes: the class translations could not be kept: {ex.Message}");
        }

        lock (_sync)
        {
            _count = mappings.Count;
            _fetched = DateTimeOffset.UtcNow;
        }

        Log.Change("themes.translations",
            $"Themes: {mappings.Count} class translations fetched from the {(beta ? "beta" : "stable")} table.");
        _apply(mappings);
        return true;
    }
}
