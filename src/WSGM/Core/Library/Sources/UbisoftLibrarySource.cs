using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>One game Ubisoft Connect lists as installed.</summary>
/// <param name="Id">The registry key's name, the game's Ubisoft id.</param>
/// <param name="InstallDir">Where it is installed, as the launcher wrote it.</param>
public sealed record UbisoftInstall(string Id, string InstallDir);

/// <summary>Finds the games Ubisoft Connect has installed.</summary>
/// <remarks>
///     <para>
///         Mirrors Playnite's Ubisoft library for what is installed: one key per game under
///         <c>HKLM\SOFTWARE\ubisoft\Launcher\Installs</c>, holding its install folder. Names and
///         executables come from the launcher's product cache, a protobuf file of YAML documents that
///         Playnite and Steam ROM Manager both read. A game the cache does not know is still listed,
///         under its folder's name, with only the launcher route.
///     </para>
///     <para>
///         The direct route comes first when the cache names an executable that exists. Ubisoft games
///         open Connect themselves when they need it, so starting the executable keeps Steam's overlay
///         on the game.
///     </para>
/// </remarks>
public sealed class UbisoftLibrarySource : ILibrarySource
{
    private const string LauncherEvidence =
        "Starts through Ubisoft Connect. WSGM follows the game, so Steam shows it running and keeps its "
        + "controller layout for as long as it runs; Steam's overlay may not reach it.";

    private const string DirectEvidence =
        "Starts the game's own executable, so Steam's overlay and controller support reach it; "
        + "the game opens Ubisoft Connect itself when it needs it.";

    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<IReadOnlyList<UbisoftInstall>> _installs;
    private readonly string _localAppData;
    private readonly Func<string, byte[]?> _readBytes;
    private readonly Func<string, ProtocolCommand?> _resolveProtocol;
    private readonly Func<IReadOnlyList<UninstallEntry>> _uninstall;

    /// <summary>Creates the source over this machine's registry and files.</summary>
    public UbisoftLibrarySource()
        : this(
            UninstallEntries.Read,
            ReadInstalls,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ReadBytes,
            File.Exists,
            Directory.Exists,
            ProtocolHandler.Resolve)
    {
    }

    /// <summary>Creates the source over injected discovery seams.</summary>
    /// <param name="uninstall">Lists Windows' installed programs.</param>
    /// <param name="installs">Lists the launcher's registered installs.</param>
    /// <param name="localAppData">The local application data folder the product cache lives under.</param>
    /// <param name="readBytes">Reads a file's bytes, or returns null when it cannot be read.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="directoryExists">Whether a folder exists.</param>
    /// <param name="resolveProtocol">Resolves the program a URI opens with, or null.</param>
    public UbisoftLibrarySource(
        Func<IReadOnlyList<UninstallEntry>> uninstall,
        Func<IReadOnlyList<UbisoftInstall>> installs,
        string localAppData,
        Func<string, byte[]?> readBytes,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        Func<string, ProtocolCommand?> resolveProtocol)
    {
        ArgumentNullException.ThrowIfNull(uninstall);
        ArgumentNullException.ThrowIfNull(installs);
        ArgumentNullException.ThrowIfNull(localAppData);
        ArgumentNullException.ThrowIfNull(readBytes);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(resolveProtocol);
        _uninstall = uninstall;
        _installs = installs;
        _localAppData = localAppData;
        _readBytes = readBytes;
        _fileExists = fileExists;
        _directoryExists = directoryExists;
        _resolveProtocol = resolveProtocol;
    }

    /// <inheritdoc />
    public string Id => "ubisoft";

    /// <inheritdoc />
    public string DisplayName => "Ubisoft Connect";

    /// <inheritdoc />
    public SourceAvailability Detect()
    {
        return LauncherFolder() is null ? SourceAvailability.NotFound : new SourceAvailability(true, "Installed");
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (LauncherFolder() is not { } launcher)
        {
            return Task.FromResult<IReadOnlyList<DiscoveredGame>>([]);
        }

        var products = ReadProducts(launcher);
        Dictionary<uint, UbisoftProduct> byId = [];
        HashSet<uint> addons = [];
        foreach (var product in products)
        {
            byId.TryAdd(product.UplayId, product);
            addons.UnionWith(product.Addons);
        }

        foreach (var product in products)
        {
            // Some installs are keyed by the install id rather than the Ubisoft id.
            byId.TryAdd(product.InstallId, product);
        }

        List<DiscoveredGame> games = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var install in _installs())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = install.InstallDir.Replace('/', '\\').TrimEnd('\\');
            if (install.Id.Length == 0 || location.Length == 0 || !seen.Add(install.Id)
                || !_directoryExists(location))
            {
                continue;
            }

            UbisoftProduct? product = null;
            if (uint.TryParse(install.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                byId.TryGetValue(id, out product);
                if (product is not null && (product.ThirdParty || product.IsUlc || !product.HasStartGame
                                            || addons.Contains(id)))
                {
                    continue;
                }
            }

            List<ShortcutRoute> routes = [];
            if (product is { Executable.Length: > 0 })
            {
                var executable = Path.Combine(location, product.Executable.Replace('/', '\\').TrimStart('\\'));
                if (_fileExists(executable))
                {
                    routes.Add(new ShortcutRoute(
                        "direct", "Game executable", executable, location, string.Empty, DirectEvidence));
                }
            }

            if (_resolveProtocol($"uplay://launch/{install.Id}/0") is { } command)
            {
                routes.Add(new ShortcutRoute(
                    "launcher", "Ubisoft Connect", command.Program,
                    Path.GetDirectoryName(command.Program) ?? string.Empty, command.Arguments, LauncherEvidence,
                    location));
            }

            if (routes.Count == 0)
            {
                continue;
            }

            games.Add(new DiscoveredGame(
                Id,
                install.Id,
                product is { Name.Length: > 0 } ? product.Name : Path.GetFileName(location),
                location,
                new GameLaunch(routes[0].Label, true, routes[0].Evidence),
                MultiplayerVerdict.Unknown,
                "The launcher does not say.",
                true,
                [],
                [],
                routes));
        }

        return Task.FromResult<IReadOnlyList<DiscoveredGame>>(games);
    }

    private IReadOnlyList<UbisoftProduct> ReadProducts(string launcher)
    {
        foreach (var path in new[]
                 {
                     Path.Combine(_localAppData, "Ubisoft Game Launcher", "cache", "configuration",
                         "configurations"),
                     Path.Combine(launcher, "cache", "configuration", "configurations")
                 })
        {
            if (_readBytes(path) is { Length: > 0 } bytes)
            {
                return UbisoftConfigurations.Parse(bytes);
            }
        }

        return [];
    }

    private string? LauncherFolder()
    {
        foreach (var entry in _uninstall())
        {
            if (entry.DisplayName is not ("Ubisoft Connect" or "Uplay") || entry.InstallLocation.Length == 0)
            {
                continue;
            }

            if (_fileExists(Path.Combine(entry.InstallLocation, "UbisoftConnect.exe"))
                || _fileExists(Path.Combine(entry.InstallLocation, "upc.exe")))
            {
                return entry.InstallLocation;
            }
        }

        return null;
    }

    private static IReadOnlyList<UbisoftInstall> ReadInstalls()
    {
        List<UbisoftInstall> installs = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = root.OpenSubKey(@"SOFTWARE\ubisoft\Launcher\Installs");
                if (key is null)
                {
                    continue;
                }

                foreach (var name in key.GetSubKeyNames())
                {
                    using var game = key.OpenSubKey(name);
                    if (game?.GetValue("InstallDir") is string { Length: > 0 } directory && seen.Add(name))
                    {
                        installs.Add(new UbisoftInstall(name, directory));
                    }
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException
                                           or IOException)
            {
                // A view the process cannot read lists nothing.
            }
        }

        return installs;
    }

    private static byte[]? ReadBytes(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>What the product cache says about one Ubisoft product.</summary>
/// <param name="UplayId">Its Ubisoft id.</param>
/// <param name="InstallId">Its install id.</param>
/// <param name="Name">Its name, localised when the cache names a token, or empty.</param>
/// <param name="Executable">Its executable relative to the install folder, or empty.</param>
/// <param name="ThirdParty">Whether another store sells and starts it.</param>
/// <param name="IsUlc">Whether it is downloadable content.</param>
/// <param name="HasStartGame">Whether it says how to start it at all.</param>
/// <param name="Addons">The ids of its add-ons, which are never games themselves.</param>
internal sealed record UbisoftProduct(
    uint UplayId,
    uint InstallId,
    string Name,
    string Executable,
    bool ThirdParty,
    bool IsUlc,
    bool HasStartGame,
    IReadOnlyList<uint> Addons);

/// <summary>Reads Ubisoft Connect's product cache.</summary>
/// <remarks>
///     The file is a protobuf message whose field 1 repeats one entry per product: field 1 the Ubisoft
///     id, field 2 the install id and field 3 a YAML document. Only that much of protobuf is read, every
///     length checked against the buffer, and anything else is skipped by its wire type.
/// </remarks>
internal static class UbisoftConfigurations
{
    /// <summary>Parses the cache.</summary>
    /// <param name="data">The file's bytes.</param>
    /// <returns>Every product that carried a YAML document, as far as the file could be read.</returns>
    public static IReadOnlyList<UbisoftProduct> Parse(ReadOnlySpan<byte> data)
    {
        List<UbisoftProduct> products = [];
        var position = 0;
        while (position < data.Length)
        {
            if (!Protobuf.TryReadVarint(data, ref position, out var tag))
            {
                break;
            }

            var wireType = (int)(tag & 7);
            if (tag >> 3 == 1 && wireType == 2)
            {
                if (!Protobuf.TryReadBytes(data, ref position, out var entry))
                {
                    break;
                }

                if (ParseEntry(entry) is { } product)
                {
                    products.Add(product);
                }
            }
            else if (!Protobuf.TrySkip(data, ref position, wireType))
            {
                break;
            }
        }

        return products;
    }

    /// <summary>Reads what discovery needs from one product's YAML document.</summary>
    /// <param name="uplayId">Its Ubisoft id.</param>
    /// <param name="installId">Its install id.</param>
    /// <param name="yaml">The document.</param>
    /// <returns>The product, or null when the document has no <c>root</c>.</returns>
    public static UbisoftProduct? Describe(uint uplayId, uint installId, string yaml)
    {
        if (MiniYaml.Parse(yaml) is not Dictionary<string, object?> document
            || MiniYaml.ChildMap(document, "root") is not { } root)
        {
            return null;
        }

        var name = MiniYaml.ChildText(root, "name");
        var localized = MiniYaml.ChildMap(MiniYaml.ChildMap(document, "localizations"), "default");
        if (name.Length > 0 && localized is not null && MiniYaml.ChildText(localized, name) is { Length: > 0 } text)
        {
            name = text;
        }

        var startGame = MiniYaml.ChildMap(root, "start_game");
        var executable = string.Empty;
        foreach (var mode in new[] { "offline", "online" })
        {
            var executables = MiniYaml.ChildList(MiniYaml.ChildMap(startGame, mode), "executables");
            if (executables is [Dictionary<string, object?> first, ..]
                && MiniYaml.ChildText(MiniYaml.ChildMap(first, "path"), "relative") is { Length: > 0 } relative)
            {
                executable = relative;
                break;
            }
        }

        List<uint> addons = [];
        foreach (var addon in MiniYaml.ChildList(root, "addons") ?? [])
        {
            if (addon is Dictionary<string, object?> map
                && uint.TryParse(MiniYaml.ChildText(map, "id"), NumberStyles.None, CultureInfo.InvariantCulture,
                    out var addonId))
            {
                addons.Add(addonId);
            }
        }

        return new UbisoftProduct(
            uplayId,
            installId,
            name,
            executable,
            root.TryGetValue("third_party_platform", out var thirdParty) && thirdParty is not null,
            MiniYaml.ChildText(root, "is_ulc") is "true" or "True" or "yes",
            startGame is not null,
            addons);
    }

    private static UbisoftProduct? ParseEntry(ReadOnlySpan<byte> data)
    {
        ulong uplayId = 0;
        ulong installId = 0;
        string? yaml = null;
        var position = 0;
        while (position < data.Length)
        {
            if (!Protobuf.TryReadVarint(data, ref position, out var tag))
            {
                return null;
            }

            var field = tag >> 3;
            var wireType = (int)(tag & 7);
            if (field is 1 or 2 && wireType == 0)
            {
                if (!Protobuf.TryReadVarint(data, ref position, out var value))
                {
                    return null;
                }

                if (field == 1)
                {
                    uplayId = value;
                }
                else
                {
                    installId = value;
                }
            }
            else if (field == 3 && wireType == 2)
            {
                if (!Protobuf.TryReadBytes(data, ref position, out var text))
                {
                    return null;
                }

                yaml = Encoding.UTF8.GetString(text);
            }
            else if (!Protobuf.TrySkip(data, ref position, wireType))
            {
                return null;
            }
        }

        return yaml is null ? null : Describe((uint)uplayId, (uint)installId, yaml);
    }
}

/// <summary>Reads the block-style subset of YAML the Ubisoft product cache is written in.</summary>
/// <remarks>
///     Mappings and sequences nested by indentation, plain and quoted scalars. Flow collections are
///     kept as their text and block scalars as empty, since discovery reads neither. Anything it
///     cannot place is skipped, so a malformed document yields less rather than failing.
/// </remarks>
internal static class MiniYaml
{
    /// <summary>Parses a document.</summary>
    /// <param name="text">The YAML text.</param>
    /// <returns>
    ///     A <see cref="Dictionary{TKey,TValue}" /> for a mapping, a <see cref="List{T}" /> for a sequence,
    ///     a string for a scalar, or null for an empty document.
    /// </returns>
    public static object? Parse(string text)
    {
        List<Line> lines = [];
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            var content = line.TrimStart(' ');
            if (content.Length == 0 || content.StartsWith('#') || content is "---" or "...")
            {
                continue;
            }

            lines.Add(new Line(line.Length - content.Length, content));
        }

        var index = 0;
        return lines.Count == 0 ? null : Block(lines, ref index, lines[0].Indent);
    }

    /// <summary>A mapping's nested mapping, or null.</summary>
    /// <param name="map">The mapping, or null.</param>
    /// <param name="key">The key.</param>
    /// <returns>The value when it is a mapping.</returns>
    public static Dictionary<string, object?>? ChildMap(Dictionary<string, object?>? map, string key)
    {
        return map is not null && map.TryGetValue(key, out var value) ? value as Dictionary<string, object?> : null;
    }

    /// <summary>A mapping's nested sequence, or null.</summary>
    /// <param name="map">The mapping, or null.</param>
    /// <param name="key">The key.</param>
    /// <returns>The value when it is a sequence.</returns>
    public static List<object?>? ChildList(Dictionary<string, object?>? map, string key)
    {
        return map is not null && map.TryGetValue(key, out var value) ? value as List<object?> : null;
    }

    /// <summary>A mapping's scalar, or empty.</summary>
    /// <param name="map">The mapping, or null.</param>
    /// <param name="key">The key.</param>
    /// <returns>The value when it is a scalar, otherwise empty.</returns>
    public static string ChildText(Dictionary<string, object?>? map, string key)
    {
        return map is not null && map.TryGetValue(key, out var value) && value is string text ? text : string.Empty;
    }

    private static object? Block(List<Line> lines, ref int index, int indent)
    {
        return IsItem(lines[index].Text) ? Sequence(lines, ref index, indent) : Mapping(lines, ref index, indent);
    }

    private static Dictionary<string, object?> Mapping(List<Line> lines, ref int index, int indent)
    {
        Dictionary<string, object?> map = new(StringComparer.Ordinal);
        while (index < lines.Count)
        {
            var line = lines[index];
            if (line.Indent < indent)
            {
                break;
            }

            if (line.Indent > indent || IsItem(line.Text) || !TrySplitKey(line.Text, out var key, out var value))
            {
                // A continuation or a line this reader cannot place.
                index++;
                continue;
            }

            index++;
            if (value.Length == 0)
            {
                var nested = index < lines.Count
                             && (lines[index].Indent > indent
                                 || (lines[index].Indent == indent && IsItem(lines[index].Text)));
                map[key] = nested ? Block(lines, ref index, lines[index].Indent) : null;
            }
            else if (value[0] is '|' or '>')
            {
                while (index < lines.Count && lines[index].Indent > indent)
                {
                    index++;
                }

                map[key] = string.Empty;
            }
            else
            {
                map[key] = Scalar(value);
            }
        }

        return map;
    }

    private static List<object?> Sequence(List<Line> lines, ref int index, int indent)
    {
        List<object?> list = [];
        while (index < lines.Count)
        {
            var line = lines[index];
            if (line.Indent > indent)
            {
                index++;
                continue;
            }

            if (line.Indent < indent || !IsItem(line.Text))
            {
                break;
            }

            var rest = line.Text[1..].TrimStart(' ');
            if (rest.Length == 0)
            {
                index++;
                list.Add(index < lines.Count && lines[index].Indent > indent
                    ? Block(lines, ref index, lines[index].Indent)
                    : null);
            }
            else if (IsItem(rest) || TrySplitKey(rest, out _, out _))
            {
                // "- key: value" opens a mapping whose keys line up with the first one.
                var nested = indent + line.Text.Length - rest.Length;
                lines[index] = new Line(nested, rest);
                list.Add(Block(lines, ref index, nested));
            }
            else
            {
                index++;
                list.Add(Scalar(rest));
            }
        }

        return list;
    }

    private static bool IsItem(string text)
    {
        return text == "-" || text.StartsWith("- ", StringComparison.Ordinal);
    }

    private static bool TrySplitKey(string text, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        string after;
        if (text[0] is '"' or '\'')
        {
            var close = text.IndexOf(text[0], 1);
            if (close < 0)
            {
                return false;
            }

            key = text[1..close];
            after = text[(close + 1)..];
        }
        else
        {
            var colon = text.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0)
            {
                if (!text.EndsWith(':'))
                {
                    return false;
                }

                colon = text.Length - 1;
            }

            key = text[..colon].TrimEnd();
            after = text[colon..];
        }

        if (key.Length == 0 || !after.StartsWith(':') || (after.Length > 1 && after[1] != ' '))
        {
            return false;
        }

        value = after[1..].Trim();
        return true;
    }

    private static string Scalar(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        return (comment >= 0 ? value[..comment] : value).Trim();
    }

    private readonly record struct Line(int Indent, string Text);
}
