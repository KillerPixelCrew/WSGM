using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace WSGM.Core;

/// <summary>Discovers installed EA games from their own installerdata.xml metadata.</summary>
/// <remarks>The two documented manifest shapes and origin2 offer URI follow SRM's EA Desktop parser.</remarks>
public sealed class EaLibrarySource : ILibrarySource
{
    /// <inheritdoc />
    public string Id => "ea";

    /// <inheritdoc />
    public string DisplayName => "EA app";

    /// <inheritdoc />
    public SourceAvailability Detect(IReadOnlyList<UninstallEntry> programs)
    {
        return new SourceAvailability(programs.Any(IsLauncher) || Roots(programs).Any(Directory.Exists),
            "EA installed-game metadata");
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(IReadOnlyList<UninstallEntry> programs,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => Discover(programs, cancellationToken), cancellationToken);
    }

    private static bool IsLauncher(UninstallEntry entry)
    {
        return entry.DisplayName.Equals("EA app", StringComparison.OrdinalIgnoreCase)
               || entry.DisplayName.Equals("EA Desktop", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Roots(IReadOnlyList<UninstallEntry> programs)
    {
        return new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EA Games"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "EA Games"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Origin Games")
            }.Concat(programs.Where(entry =>
                entry.Publisher.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase)
                && entry.InstallLocation.Length > 0).Select(entry => entry.InstallLocation))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private IReadOnlyList<DiscoveredGame> Discover(IReadOnlyList<UninstallEntry> programs, CancellationToken token)
    {
        Dictionary<string, DiscoveredGame> games = new(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Roots(programs).Where(Directory.Exists))
        {
            foreach (var directory in new[] { root }.Concat(LibraryFiles.Directories(root)))
            {
                token.ThrowIfCancellationRequested();
                if (ShortcutFolderSource.IsSteam(directory))
                {
                    continue;
                }

                var manifest = Path.Combine(directory, "__Installer", "installerdata.xml");
                if (!File.Exists(manifest))
                {
                    continue;
                }

                try
                {
                    var bytes = LibraryFiles.ReadBytes(manifest);
                    if (bytes is null)
                    {
                        continue;
                    }

                    using var stream = new MemoryStream(bytes);
                    using var reader = XmlReader.Create(stream);
                    var document = XDocument.Load(reader);

                    string? Value(string name)
                    {
                        return document.Descendants().FirstOrDefault(element =>
                            element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim();
                    }

                    var key = Value("contentID");
                    var name = Value("gameTitle") ?? Value("title");
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    List<ShortcutRoute> routes = [];
                    var uri = "origin2://game/launch/?offerIds=" + Uri.EscapeDataString(key);
                    if (ProtocolHandler.Resolve(uri) is { } handler)
                    {
                        routes.Add(ShortcutRoute.ThroughLauncher(handler, "EA app",
                            ShortcutRoute.FollowedLauncherEvidence("EA app"), directory));
                    }

                    if (Value("filePath") is { Length: > 0 } relative)
                    {
                        relative = Regex.Replace(relative, @"^\[[^\]]*\]", "").TrimStart('\\', '/');
                        var executable = LibraryFiles.Under(directory, relative);
                        if (File.Exists(executable))
                        {
                            routes.Add(new ShortcutRoute("direct", "Game executable", executable, directory,
                                Value("parameters") ?? "",
                                ShortcutRoute.DirectEvidence + " EA may still require its launcher."));
                        }
                    }

                    if (routes.Count > 0)
                    {
                        games.TryAdd(key, DiscoveredGame.Command(this, key, name, directory, routes));
                    }
                }
                catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException
                                               or ArgumentException or InvalidOperationException)
                {
                    Log.Warn($"EA metadata for {Path.GetFileName(directory)} could not be read: {ex.Message}");
                }
            }
        }

        return games.Values.ToArray();
    }
}
