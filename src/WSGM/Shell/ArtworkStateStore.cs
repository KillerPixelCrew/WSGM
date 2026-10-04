using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using WSGM.Core;

namespace WSGM.Shell;

internal sealed class ArtworkStateStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private ArtworkState? _state;

    internal ArtworkStateStore(string root)
    {
        _path = Path.Combine(root, "artwork.json");
    }

    internal ArtworkGameLink? FindGame(uint appId)
    {
        lock (_gate)
        {
            return Read().Games.FirstOrDefault(link => link.AppId == appId);
        }
    }

    internal IReadOnlyList<ArtworkSavedFilter> ReadFilters()
    {
        lock (_gate)
        {
            return [.. Read().Filters];
        }
    }

    internal void SaveFilter(string tab, SteamArtworkBrowserFilter filter)
    {
        lock (_gate)
        {
            var state = Read();
            state.Filters.RemoveAll(saved => saved.Tab == tab);
            state.Filters.Add(new ArtworkSavedFilter
            {
                Tab = tab,
                Styles = [.. filter.Styles],
                Dimensions = [.. filter.Dimensions],
                Mimes = [.. filter.Mimes],
                Static = filter.Static,
                Animated = filter.Animated,
                Adult = filter.Adult,
                Humor = filter.Humor,
                Epilepsy = filter.Epilepsy,
                Untagged = filter.Untagged
            });
            Write(state);
        }
    }

    internal void SaveGame(uint appId, ArtworkGameMatch? match)
    {
        lock (_gate)
        {
            var state = Read();
            state.Games.RemoveAll(link => link.AppId == appId);
            if (match is not null)
            {
                state.Games.Add(new ArtworkGameLink
                {
                    AppId = appId,
                    ProviderId = match.ProviderId,
                    GameId = match.Id,
                    Name = match.Name
                });
            }

            Write(state);
        }
    }

    private ArtworkState Read()
    {
        if (_state is not null)
        {
            return _state;
        }

        try
        {
            using var stream = File.OpenRead(_path);
            _state = JsonSerializer.Deserialize(stream, ArtworkStateJsonContext.Default.ArtworkState)
                     ?? throw new JsonException("Artwork state is null.");
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (JsonException ex)
        {
            try
            {
                File.Move(_path, _path + ".corrupt-" + Guid.NewGuid().ToString("N"));
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                throw new ConfigUnavailableException("Damaged artwork state could not be set aside.", failure);
            }

            Log.Warn($"Artwork state was set aside after a parse failure: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigUnavailableException("Artwork state is unreadable; it will not be overwritten.", ex);
        }

        _state ??= new ArtworkState();
        _state.Games ??= [];
        _state.Filters ??= [];
        _state.Games =
        [
            .. _state.Games.Where(link => link is not null && link.AppId != 0
                                                           && !string.IsNullOrWhiteSpace(link.ProviderId)
                                                           && !string.IsNullOrWhiteSpace(link.GameId)
                                                           && link.Name is not null)
        ];
        _state.Filters =
        [
            .. _state.Filters.Where(filter => filter is not null && !string.IsNullOrWhiteSpace(filter.Tab))
        ];
        foreach (var filter in _state.Filters)
        {
            filter.Styles ??= [];
            filter.Dimensions ??= [];
            filter.Mimes ??= [];
        }
        return _state;
    }

    private static void EnsureDirectory(string file)
    {
        var directory = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private void Write(ArtworkState state)
    {
        try
        {
            EnsureDirectory(_path);
            AtomicFile.Write(
                _path,
                stream =>
                {
                    JsonSerializer.Serialize(stream, state, ArtworkStateJsonContext.Default.ArtworkState);
                    return true;
                },
                false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _state = null;
            throw new ConfigUnavailableException("Artwork state could not be saved.", ex);
        }
    }
}

internal sealed class ArtworkState
{
    public List<ArtworkGameLink> Games { get; set; } = [];
    public List<ArtworkSavedFilter> Filters { get; set; } = [];
}

internal sealed class ArtworkGameLink
{
    public uint AppId { get; set; }
    public string ProviderId { get; set; } = "";
    public string GameId { get; set; } = "";
    public string Name { get; set; } = "";
}

internal sealed class ArtworkSavedFilter
{
    public string Tab { get; set; } = "";
    public List<string> Styles { get; set; } = [];
    public List<string> Dimensions { get; set; } = [];
    public List<string> Mimes { get; set; } = [];
    public bool Static { get; set; } = true;
    public bool Animated { get; set; } = true;
    public bool Adult { get; set; }
    public bool Humor { get; set; } = true;
    public bool Epilepsy { get; set; } = true;
    public bool Untagged { get; set; } = true;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ArtworkState))]
internal sealed partial class ArtworkStateJsonContext : JsonSerializerContext;
