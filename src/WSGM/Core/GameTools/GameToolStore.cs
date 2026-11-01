using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>The durable Game Tools state.</summary>
internal sealed record GameToolStore
{
    /// <summary>The format version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The per-game records.</summary>
    public List<GameToolRecord> Games { get; init; } = [];

    /// <summary>Finds a game's record.</summary>
    /// <param name="appId">The AppID.</param>
    /// <returns>The record or null.</returns>
    internal GameToolRecord? Find(uint appId)
    {
        return Games.FirstOrDefault(game => game.AppId == appId);
    }

    /// <summary>Replaces or removes a game's record, dropping empty records.</summary>
    /// <param name="appId">The AppID.</param>
    /// <param name="record">The new record, or null to remove it.</param>
    /// <returns>The changed store.</returns>
    internal GameToolStore With(uint appId, GameToolRecord? record)
    {
        List<GameToolRecord> games = [.. Games.Where(game => game.AppId != appId)];
        if (record is not null && (record.Runtime is not null || record.Tools.Count > 0))
        {
            games.Add(record);
        }

        return this with { Games = games };
    }
}
