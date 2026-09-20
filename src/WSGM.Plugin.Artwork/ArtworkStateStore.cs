using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WSGM.Plugin.Artwork;

internal sealed class ArtworkStateStore
{
    private readonly object _gate = new();
    private string? _path;
    private ArtworkPluginState? _state;

    internal void SetDirectory(string stateDirectory)
    {
        lock (_gate)
        {
            _path = Path.Combine(Path.GetFullPath(stateDirectory), "artwork.json");
            _state = null;
        }
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

    private ArtworkPluginState Read()
    {
        if (_state is not null)
        {
            return _state;
        }

        try
        {
            if (_path is { } path && File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                _state = JsonSerializer.Deserialize(stream, ArtworkStateJsonContext.Default.ArtworkPluginState);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ArtworkLog.Warn($"State load failed: {ex.Message}");
        }

        _state ??= new ArtworkPluginState();
        _state.Games ??= [];
        _state.Filters ??= [];
        _state.Games =
        [
            .. _state.Games.Where(link => link is not null && link.AppId != 0
                                                           && link.ProviderId.Length is > 0 and <= 128
                                                           && link.GameId.Length is > 0 and <= 128
                                                           && link.Name.Length <= 256).Take(2048)
        ];
        _state.Filters =
        [
            .. _state.Filters.Where(filter => filter is not null
                                              && filter.Tab.Length is > 0 and <= 32).Take(16)
        ];
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

    private void Write(ArtworkPluginState state)
    {
        if (_path is not { } path)
        {
            return;
        }

        EnsureDirectory(path);
        var temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, ArtworkStateJsonContext.Default.ArtworkPluginState);
            }

            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ArtworkLog.Warn($"State save failed: {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception)
            {
                // A future save retries the same bounded temporary path.
            }
        }
    }
}

internal sealed class ArtworkPluginState
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
[JsonSerializable(typeof(ArtworkPluginState))]
internal sealed partial class ArtworkStateJsonContext : JsonSerializerContext;
