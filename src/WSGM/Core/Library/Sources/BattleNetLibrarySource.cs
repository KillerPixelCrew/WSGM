using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Finds the Blizzard games Battle.net has installed on this machine.</summary>
/// <remarks>
///     <para>
///         Follows Playnite's Battle.net library. Each game Battle.net installs registers an
///         uninstall entry whose command carries its internal id as <c>--uid=</c>, and a fixed table
///         maps that id to the product code <c>Battle.net.exe --exec="launch …"</c> takes. An id the
///         table does not know is skipped, because there is no product code to launch it with.
///     </para>
///     <para>
///         An id matches a table entry when it starts with the entry's internal id and what follows is
///         nothing, more of the same word (<c>fenris</c> for Diablo IV's <c>Fen</c>) or a locale such
///         as <c>_enus</c>. Anything else after an underscore is another product sharing the prefix:
///         <c>wow_classic</c> is not World of Warcraft, and launching it by WoW's code would start the
///         wrong game. The agent's own <c>product.db</c> is matched by the same rule.
///     </para>
///     <para>
///         The classic Diablo II and Warcraft III installs predate that scheme. They register under
///         their own name with Blizzard as publisher and start from their own executable, so they are
///         offered whether or not Battle.net itself is installed. Lord of Destruction is offered only
///         when its data file is in the install, since the base game alone starts the same executable.
///     </para>
/// </remarks>
public sealed partial class BattleNetLibrarySource : ILibrarySource
{
    private const string ClientExecutable = "Battle.net.exe";

    private const string LauncherLabel = "Battle.net";

    private const string DirectLabel = "Game executable";

    /// <summary>
    ///     Playnite's product table, in its order, which matters: a uid is matched by prefix and the
    ///     first entry wins. Playnite lists the two Warcraft remasters twice; they appear once here.
    /// </summary>
    private static readonly BattleNetProduct[] Products =
    [
        new("WoW", "wow", "World of Warcraft"),
        new("D3", "diablo3", "Diablo III"),
        new("S2", "s2", "StarCraft II"),
        new("S1", "s1", "StarCraft"),
        new("WTCG", "hs_beta", "Hearthstone"),
        new("Hero", "heroes", "Heroes of the Storm"),
        new("Pro", "prometheus", "Overwatch 2"),
        new("D2", "Diablo II", "Diablo II", "Diablo II.exe"),
        new("D2X", "Diablo II", "Diablo II: Lord of Destruction", "Diablo II.exe", "d2exp.mpq"),
        new("VIPR", "viper", "Call of Duty: Black Ops 4"),
        new("ODIN", "odin", "Call of Duty: Modern Warfare"),
        new("W3C", "Warcraft III", "Warcraft III: Reign of Chaos", "Warcraft III.exe"),
        new("W3CX", "Warcraft III", "Warcraft III: The Frozen Throne", "Frozen Throne.exe"),
        new("W3", "w3", "Warcraft III: Reforged"),
        new("LAZR", "lazarus", "Call of Duty: Modern Warfare 2 Campaign Remastered"),
        new("ZEUS", "zeus", "Call of Duty: Black Ops Cold War"),
        new("WLBY", "wlby", "Crash Bandicoot 4"),
        new("OSI", "osi", "Diablo II: Resurrected"),
        new("RTRO", "rtro", "Blizzard Arcade Collection"),
        new("FORE", "fore", "Call of Duty: Vanguard"),
        new("ANBS", "anbs", "Diablo Immortal"),
        new("AUKS", "auks", "Call of Duty: Modern Warfare II"),
        new("Fen", "Fen", "Diablo IV"),
        new("D1", "D1", "Diablo"),
        new("W1R", "w1r", "Warcraft: Remastered"),
        new("W2R", "w2r", "Warcraft II: Remastered"),
        new("W1", "w1", "Warcraft: Orcs & Humans"),
        new("W2", "w2", "Warcraft II: Battle.net Edition"),
        new("GRY", "gryphon", "Warcraft Rumble"),
        new("ARIS", "aris", "Doom: The Dark Ages"),
        new("SCOR", "scorpio", "Sea of Thieves"),
        new("ARK", "arkansas", "The Outer Worlds 2"),
        new("LBRA", "libra", "Tony Hawk's Pro Skater 3 + 4"),
        new("PNTA", "pinta", "Call of Duty: Modern Warfare III"),
        new("AQUA", "aqua", "Avowed")
    ];

    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<byte[]?> _readProducts;
    private readonly Func<IReadOnlyList<UninstallEntry>> _uninstall;

    /// <summary>Creates the source over this machine's registry and file system.</summary>
    public BattleNetLibrarySource()
        : this(UninstallEntries.Read, File.Exists, Directory.Exists, ReadProductDatabase)
    {
    }

    /// <summary>Creates the source over injected discovery seams.</summary>
    /// <param name="uninstall">Lists Windows' uninstall entries.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="directoryExists">Whether a folder exists.</param>
    /// <param name="readProducts">Reads the agent's <c>product.db</c>, or returns null.</param>
    internal BattleNetLibrarySource(
        Func<IReadOnlyList<UninstallEntry>> uninstall,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        Func<byte[]?> readProducts)
    {
        ArgumentNullException.ThrowIfNull(uninstall);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(readProducts);
        _uninstall = uninstall;
        _fileExists = fileExists;
        _directoryExists = directoryExists;
        _readProducts = readProducts;
    }

    /// <inheritdoc />
    public string Id => "battlenet";

    /// <inheritdoc />
    public string DisplayName => "Battle.net";

    /// <inheritdoc />
    /// <remarks>The classic games run without Battle.net, so one of them alone makes the source available.</remarks>
    public SourceAvailability Detect()
    {
        var entries = _uninstall();
        if (FindClient(entries) is not null)
        {
            return new SourceAvailability(true, "Installed");
        }

        return entries.Any(entry => ClassicGames(entry).Any())
            ? new SourceAvailability(true, "Games only")
            : SourceAvailability.NotFound;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => Discover(cancellationToken), cancellationToken);
    }

    /// <summary>The product code a Battle.net internal id launches by.</summary>
    /// <param name="uid">The id an uninstall entry or <c>product.db</c> carries.</param>
    /// <returns>The product code, or null when the table has no product for it.</returns>
    internal static string? ProductCode(string uid)
    {
        return Product(uid)?.ProductId;
    }

    /// <summary>Reads the installs the Battle.net agent records.</summary>
    /// <param name="data">The contents of <c>product.db</c>, or null.</param>
    /// <returns>Each install's uid and path, as far as the file could be read.</returns>
    /// <remarks>
    ///     The layout Playnite reads it with: field 1 repeats per install, holding the uid in field 1,
    ///     the product code in field 2 and, in field 3, a settings message whose field 1 is the path.
    /// </remarks>
    internal static IReadOnlyList<(string Uid, string Path)> ParseProducts(byte[]? data)
    {
        List<(string Uid, string Path)> installs = [];
        if (data is null)
        {
            return installs;
        }

        ReadOnlySpan<byte> bytes = data;
        var position = 0;
        while (position < bytes.Length && Protobuf.TryReadVarint(bytes, ref position, out var tag))
        {
            var wireType = (int)(tag & 7);
            if (tag >> 3 != 1 || wireType != 2)
            {
                if (!Protobuf.TrySkip(bytes, ref position, wireType))
                {
                    break;
                }

                continue;
            }

            if (!Protobuf.TryReadBytes(bytes, ref position, out var install))
            {
                break;
            }

            if (ParseInstall(install) is { } parsed)
            {
                installs.Add(parsed);
            }
        }

        return installs;
    }

    private IReadOnlyList<DiscoveredGame> Discover(CancellationToken cancellationToken)
    {
        var entries = _uninstall();
        var client = FindClient(entries);
        List<DiscoveredGame> found = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var product in ClassicGames(entry))
            {
                var executable = Path.Combine(entry.InstallLocation, product.ClassicExecutable!);
                if (_fileExists(executable) && seen.Add(product.ProductId))
                {
                    found.Add(DiscoveredGame.Command(Id, product.ProductId, product.Name, entry.InstallLocation,
                    [
                        new ShortcutRoute(
                            "direct", DirectLabel, executable, entry.InstallLocation, string.Empty,
                            ShortcutRoute.DirectEvidence)
                    ]));
                }
            }
        }

        if (client is null)
        {
            // Everything else starts through Battle.net, which is not here to start it.
            return found;
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.UninstallString.Length == 0 || entry.InstallLocation.Length == 0
                                                  || entry.DisplayName.EndsWith("Test", StringComparison.Ordinal)
                                                  || entry.DisplayName.EndsWith("Beta", StringComparison.Ordinal)
                                                  || UidPattern().Match(entry.UninstallString) is not
                                                      { Success: true } match)
            {
                continue;
            }

            AddLaunched(found, seen, client, match.Groups[1].Value, entry.InstallLocation);
        }

        // Battle.net does not always write an uninstall entry for a game, so its agent's own record of
        // installs is read as well, as Playnite does: product.db, a protobuf list of installs, each
        // with its uid and install path. Only a game the uninstall list did not already give is added.
        foreach (var (uid, path) in ParseProducts(_readProducts()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddLaunched(found, seen, client, uid, path);
        }

        return found;
    }

    /// <summary>Adds the game a Battle.net uid names, launched through the client, once.</summary>
    private void AddLaunched(
        List<DiscoveredGame> found, HashSet<string> seen, string client, string uid, string installPath)
    {
        var location = LibraryFiles.InstallFolder(installPath);
        if (Product(uid) is not { } product || location.Length == 0 || !_directoryExists(location)
            || !seen.Add(product.ProductId))
        {
            return;
        }

        found.Add(DiscoveredGame.Command(Id, product.ProductId, product.Name, location,
        [
            ShortcutRoute.ThroughLauncher(
                client,
                LaunchArguments.Named("--exec=", "launch " + product.ProductId),
                LauncherLabel,
                ShortcutRoute.FollowedLauncherEvidence(LauncherLabel),
                location)
        ]));
    }

    /// <summary>The classic games an uninstall entry installed, when it is one.</summary>
    private IEnumerable<BattleNetProduct> ClassicGames(UninstallEntry entry)
    {
        if (entry.Publisher != "Blizzard Entertainment" || entry.InstallLocation.Length == 0
                                                        || UidPattern().IsMatch(entry.UninstallString))
        {
            return [];
        }

        return Products.Where(candidate => candidate.Classic
                                           && entry.DisplayName == candidate.InternalId
                                           && (candidate.ClassicMarker is null
                                               || _fileExists(Path.Combine(entry.InstallLocation,
                                                   candidate.ClassicMarker))));
    }

    private static BattleNetProduct? Product(string uid)
    {
        return Products.FirstOrDefault(candidate => !candidate.Classic && Matches(uid, candidate.InternalId));
    }

    /// <summary>Whether a uid names the product with this internal id, not another sharing its prefix.</summary>
    private static bool Matches(string uid, string internalId)
    {
        if (!uid.StartsWith(internalId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = uid[internalId.Length..];
        if (rest.Length == 0)
        {
            return true;
        }

        // A locale suffix, such as _enus, still names the same product.
        if (rest[0] == '_')
        {
            return rest.Length == 5 && rest[1..].All(char.IsAsciiLetter);
        }

        return rest.All(char.IsAsciiLetterOrDigit);
    }

    private static (string Uid, string Path)? ParseInstall(ReadOnlySpan<byte> data)
    {
        var uid = string.Empty;
        var path = string.Empty;
        var position = 0;
        while (position < data.Length)
        {
            if (!Protobuf.TryReadVarint(data, ref position, out var tag))
            {
                return null;
            }

            var field = tag >> 3;
            var wireType = (int)(tag & 7);
            if (wireType == 2 && field is 1 or 3)
            {
                if (!Protobuf.TryReadBytes(data, ref position, out var value))
                {
                    return null;
                }

                if (field == 1)
                {
                    uid = Encoding.UTF8.GetString(value);
                }
                else
                {
                    path = FirstString(value);
                }
            }
            else if (!Protobuf.TrySkip(data, ref position, wireType))
            {
                return null;
            }
        }

        return uid.Length > 0 ? (uid, path) : null;
    }

    /// <summary>A message's field 1 as text: the settings message's install path.</summary>
    private static string FirstString(ReadOnlySpan<byte> data)
    {
        var position = 0;
        while (position < data.Length && Protobuf.TryReadVarint(data, ref position, out var tag))
        {
            var wireType = (int)(tag & 7);
            if (tag >> 3 == 1 && wireType == 2)
            {
                return Protobuf.TryReadBytes(data, ref position, out var value)
                    ? Encoding.UTF8.GetString(value)
                    : string.Empty;
            }

            if (!Protobuf.TrySkip(data, ref position, wireType))
            {
                break;
            }
        }

        return string.Empty;
    }

    private static byte[]? ReadProductDatabase()
    {
        return LibraryFiles.ReadBytes(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Battle.net", "Agent", "product.db"));
    }

    /// <summary>The Battle.net client's executable, or null when it is not installed.</summary>
    private string? FindClient(IReadOnlyList<UninstallEntry> entries)
    {
        return UninstallEntries.FindProgram(
            entries,
            entry => entry.UninstallString.Contains("-uid=battle.net", StringComparison.OrdinalIgnoreCase),
            _fileExists,
            ClientExecutable);
    }

    /// <summary>Playnite's pattern for a game Battle.net installed, capturing its internal id.</summary>
    [GeneratedRegex("""Battle\.net.*--uid=([^\s"]+)""")]
    private static partial Regex UidPattern();

    /// <summary>One entry of the product table.</summary>
    /// <param name="ProductId">The product code Battle.net launches it by, and its key here.</param>
    /// <param name="InternalId">
    ///     The uid prefix its uninstall entry carries, or for a classic game the entry's display name.
    /// </param>
    /// <param name="Name">What to call it.</param>
    /// <param name="ClassicExecutable">The executable a classic game starts from, or null.</param>
    /// <param name="ClassicMarker">A file only this classic game's install has, or null when none is needed.</param>
    private sealed record BattleNetProduct(
        string ProductId,
        string InternalId,
        string Name,
        string? ClassicExecutable = null,
        string? ClassicMarker = null)
    {
        /// <summary>Whether it is a classic game that starts from its own executable.</summary>
        public bool Classic => ClassicExecutable is not null;
    }
}
