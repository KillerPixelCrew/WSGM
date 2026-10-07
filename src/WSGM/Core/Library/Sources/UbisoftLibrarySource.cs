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
///         Follows Playnite's Ubisoft library for what is installed: one key per game under
///         <c>HKLM\SOFTWARE\ubisoft\Launcher\Installs</c>, holding its install folder. Names, executables
///         and working folders come from the launcher's product cache, a protobuf file of YAML documents
///         that Playnite and Steam ROM Manager both read. A game the cache does not know is still
///         listed, under its folder's name, with only the launcher route.
///     </para>
///     <para>
///         The cache is also what tells a game from an add-on or from a game another store sells and
///         only borrows Connect for, both of which register an install too. When there are installs
///         and the cache cannot be read, the scan of this source fails rather than offering every
///         install as a game: Connect writes the cache the first time it starts.
///     </para>
///     <para>
///         The direct route comes first when the cache names an executable that exists. Ubisoft games
///         open Connect themselves when they need it, so starting the executable keeps Steam's overlay
///         on the game.
///     </para>
/// </remarks>
public sealed class UbisoftLibrarySource : ILibrarySource
{
    private const string LauncherName = "Ubisoft Connect";

    private const string DirectEvidence =
        ShortcutRoute.DirectEvidence + " The game opens Ubisoft Connect itself when it needs it.";

    /// <summary>The largest product cache read; it holds one YAML document per product Ubisoft sells.</summary>
    private const long MaximumCacheBytes = 64 * 1024 * 1024;

    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<IReadOnlyList<UbisoftInstall>> _installs;
    private readonly string _localAppData;
    private readonly Func<string, byte[]?> _readBytes;
    private readonly Func<string, ProtocolCommand?> _resolveProtocol;

    /// <summary>Creates the source over this machine's registry and files.</summary>
    public UbisoftLibrarySource()
        : this(
            ReadInstalls,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            path => LibraryFiles.ReadBytes(path, MaximumCacheBytes),
            File.Exists,
            Directory.Exists,
            ProtocolHandler.Resolve)
    {
    }

    /// <summary>Creates the source over injected discovery seams.</summary>
    /// <param name="installs">Lists the launcher's registered installs.</param>
    /// <param name="localAppData">The local application data folder the product cache lives under.</param>
    /// <param name="readBytes">Reads a file's bytes, or returns null when it cannot be read.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="directoryExists">Whether a folder exists.</param>
    /// <param name="resolveProtocol">Resolves the program a URI opens with, or null.</param>
    internal UbisoftLibrarySource(
        Func<IReadOnlyList<UbisoftInstall>> installs,
        string localAppData,
        Func<string, byte[]?> readBytes,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        Func<string, ProtocolCommand?> resolveProtocol)
    {
        ArgumentNullException.ThrowIfNull(installs);
        ArgumentNullException.ThrowIfNull(localAppData);
        ArgumentNullException.ThrowIfNull(readBytes);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(resolveProtocol);
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
    public string DisplayName => LauncherName;

    /// <inheritdoc />
    public SourceAvailability Detect(IReadOnlyList<UninstallEntry> programs)
    {
        return LauncherFolder(programs) is null
            ? SourceAvailability.NotFound
            : new SourceAvailability(true, "Installed");
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(
        IReadOnlyList<UninstallEntry> programs, CancellationToken cancellationToken)
    {
        return Task.Run(() => Discover(programs, cancellationToken), cancellationToken);
    }

    private IReadOnlyList<DiscoveredGame> Discover(
        IReadOnlyList<UninstallEntry> programs, CancellationToken cancellationToken)
    {
        if (LauncherFolder(programs) is not { } launcher)
        {
            return [];
        }

        var installs = _installs();
        if (installs.Count == 0)
        {
            return [];
        }

        var products = ReadProducts(launcher)
                       ?? throw new InvalidDataException(
                           "Ubisoft Connect's product cache could not be read, so its games cannot be told from "
                           + "add-ons and from games other stores sell. Start Ubisoft Connect once, then scan again.");
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
        foreach (var install in installs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = LibraryFiles.InstallFolder(install.InstallDir);
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
                var executable = LibraryFiles.Under(location, product.Executable);
                if (_fileExists(executable))
                {
                    // The cache names the working folder relative to the install; without one the game
                    // starts where its executable is.
                    var directory = product.WorkingDirectory is { } working
                        ? LibraryFiles.InstallFolder(LibraryFiles.Under(location, working))
                        : Path.GetDirectoryName(executable) ?? location;
                    routes.Add(new ShortcutRoute(
                        "direct", "Game executable", executable, directory, string.Empty, DirectEvidence));
                }
            }

            if (_resolveProtocol($"uplay://launch/{install.Id}/0") is { } command)
            {
                routes.Add(ShortcutRoute.ThroughLauncher(
                    command, LauncherName, ShortcutRoute.FollowedLauncherEvidence(LauncherName), location));
            }

            if (routes.Count > 0)
            {
                games.Add(DiscoveredGame.Command(this,
                    install.Id,
                    product is { Name.Length: > 0 } ? product.Name : Path.GetFileName(location),
                    location,
                    routes));
            }
        }

        return games;
    }

    /// <summary>The product cache's entries, or null when no cache could be read and parsed.</summary>
    private IReadOnlyList<UbisoftProduct>? ReadProducts(string launcher)
    {
        foreach (var path in new[]
                 {
                     Path.Combine(_localAppData, "Ubisoft Game Launcher", "cache", "configuration",
                         "configurations"),
                     Path.Combine(launcher, "cache", "configuration", "configurations")
                 })
        {
            if (_readBytes(path) is { Length: > 0 } bytes
                && UbisoftConfigurations.Parse(bytes) is { Count: > 0 } products)
            {
                return products;
            }
        }

        return null;
    }

    private string? LauncherFolder(IReadOnlyList<UninstallEntry> programs)
    {
        var program = UninstallEntries.FindProgram(
            programs,
            entry => entry.DisplayName is "Ubisoft Connect" or "Uplay",
            _fileExists,
            "UbisoftConnect.exe",
            "upc.exe");
        return program is null ? null : Path.GetDirectoryName(program);
    }

    private static IReadOnlyList<UbisoftInstall> ReadInstalls()
    {
        List<UbisoftInstall> installs = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            RegistryKey? key;
            string[] names;
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                key = root.OpenSubKey(@"SOFTWARE\ubisoft\Launcher\Installs");
                if (key is null)
                {
                    continue;
                }

                names = key.GetSubKeyNames();
            }
            catch (Exception ex) when (IsUnreadable(ex))
            {
                // A view the process cannot read lists nothing.
                continue;
            }

            using (key)
            {
                foreach (var name in names)
                {
                    // One key the process may not open is one install fewer, not a view fewer.
                    try
                    {
                        using var game = key.OpenSubKey(name);
                        if (game?.GetValue("InstallDir") is string { Length: > 0 } directory && seen.Add(name))
                        {
                            installs.Add(new UbisoftInstall(name, directory));
                        }
                    }
                    catch (Exception ex) when (IsUnreadable(ex))
                    {
                        Log.Debug($"Ubisoft Connect install {name} could not be read: {ex.Message}");
                    }
                }
            }
        }

        return installs;
    }

    private static bool IsUnreadable(Exception ex)
    {
        return ex is SecurityException or UnauthorizedAccessException or IOException;
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
/// <param name="WorkingDirectory">
///     The executable's working folder relative to the install folder, empty for the install folder
///     itself, or null when the cache names none.
/// </param>
internal sealed record UbisoftProduct(
    uint UplayId,
    uint InstallId,
    string Name,
    string Executable,
    bool ThirdParty,
    bool IsUlc,
    bool HasStartGame,
    IReadOnlyList<uint> Addons,
    string? WorkingDirectory = null);

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
        string? workingDirectory = null;
        foreach (var mode in new[] { "offline", "online" })
        {
            var executables = MiniYaml.ChildList(MiniYaml.ChildMap(startGame, mode), "executables");
            if (executables is [Dictionary<string, object?> first, ..]
                && MiniYaml.ChildText(MiniYaml.ChildMap(first, "path"), "relative") is { Length: > 0 } relative)
            {
                executable = relative;

                // The working folder is the install folder, registered by its registry value, with an
                // optional relative part appended, such as bin\.
                if (MiniYaml.ChildMap(first, "working_directory") is { } working)
                {
                    workingDirectory = MiniYaml.ChildText(working, "append");
                }

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
            addons,
            workingDirectory);
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
///     cannot place is skipped, so a malformed document yields less rather than failing. Nesting
///     deeper than <see cref="MaximumDepth" /> is skipped too: every level is a recursive call, and a
///     corrupted cache must not overflow the stack, which no handler can catch.
/// </remarks>
internal static class MiniYaml
{
    /// <summary>The deepest nesting read; the cache's own documents nest a handful of levels.</summary>
    internal const int MaximumDepth = 64;

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
        return lines.Count == 0 ? null : Block(lines, ref index, lines[0].Indent, 0);
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

    private static object? Block(List<Line> lines, ref int index, int indent, int depth)
    {
        if (depth > MaximumDepth)
        {
            while (index < lines.Count && lines[index].Indent >= indent)
            {
                index++;
            }

            return null;
        }

        return IsItem(lines[index].Text)
            ? Sequence(lines, ref index, indent, depth)
            : Mapping(lines, ref index, indent, depth);
    }

    private static Dictionary<string, object?> Mapping(List<Line> lines, ref int index, int indent, int depth)
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
                map[key] = nested ? Block(lines, ref index, lines[index].Indent, depth + 1) : null;
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

    private static List<object?> Sequence(List<Line> lines, ref int index, int indent, int depth)
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
                    ? Block(lines, ref index, lines[index].Indent, depth + 1)
                    : null);
            }
            else if (IsItem(rest) || TrySplitKey(rest, out _, out _))
            {
                // "- key: value" opens a mapping whose keys line up with the first one.
                var nested = indent + line.Text.Length - rest.Length;
                lines[index] = new Line(nested, rest);
                list.Add(Block(lines, ref index, nested, depth + 1));
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
