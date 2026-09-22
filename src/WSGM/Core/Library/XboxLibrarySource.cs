using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>One installed package, as the enumerator reports it.</summary>
/// <param name="Aumid">Its application user model id.</param>
/// <param name="FamilyName">Its package family name.</param>
/// <param name="DisplayName">The name Windows shows for it, or empty.</param>
/// <param name="InstallPath">Where its files are, or empty when unreadable.</param>
/// <param name="ApplicationId">The application half of the AUMID.</param>
public sealed record InstalledPackage(
    string Aumid,
    string FamilyName,
    string DisplayName,
    string InstallPath,
    string ApplicationId);

/// <summary>Finds the Xbox, UWP and MSIX games installed on this machine.</summary>
/// <remarks>
///     <para>
///         Enumeration and file reads are injected, so every classification rule is tested against
///         fixtures rather than against whatever this machine happens to have installed.
///     </para>
///     <para>
///         "Is this a game?" has no reliable offline answer for a pure UWP title: nothing in an
///         Appx manifest says so. GDK evidence is conclusive, and for everything else the Store
///         catalog decides. A title neither can vouch for is still listed, marked as not a game and
///         left unselected, rather than hidden — a user who knows better can still import it.
///     </para>
/// </remarks>
public sealed class XboxLibrarySource : ILibrarySource
{
    /// <summary>Publisher prefixes that are Windows itself rather than anything installed.</summary>
    private static readonly string[] SystemPublishers =
    [
        "Microsoft.Windows.", "MicrosoftWindows.", "Microsoft.VCLibs.", "Microsoft.NET.",
        "Microsoft.UI.", "Microsoft.Services.", "Microsoft.Advertising.", "MicrosoftCorporationII.",
        "Microsoft.WindowsAppRuntime", "Microsoft.GamingServices", "Microsoft.XboxIdentityProvider",
        "Microsoft.XboxSpeechToTextOverlay", "Microsoft.XboxGameOverlay", "Microsoft.XboxGamingOverlay"
    ];

    private readonly Func<CancellationToken, IReadOnlyList<InstalledPackage>> _enumerate;
    private readonly Func<string, string?> _readFile;
    private readonly Func<InstalledPackage, CancellationToken, Task<StoreCatalogEntry?>>? _lookUp;

    /// <summary>Creates the source over injected discovery seams.</summary>
    /// <param name="enumerate">Lists installed packages.</param>
    /// <param name="readFile">Reads a file's text, or returns null when it cannot be read.</param>
    /// <param name="lookUp">Looks a title up in the Store catalog, or null to skip that entirely.</param>
    public XboxLibrarySource(
        Func<CancellationToken, IReadOnlyList<InstalledPackage>> enumerate,
        Func<string, string?> readFile,
        Func<InstalledPackage, CancellationToken, Task<StoreCatalogEntry?>>? lookUp = null)
    {
        ArgumentNullException.ThrowIfNull(enumerate);
        ArgumentNullException.ThrowIfNull(readFile);
        _enumerate = enumerate;
        _readFile = readFile;
        _lookUp = lookUp;
    }

    /// <inheritdoc />
    public string Id => "xbox";

    /// <inheritdoc />
    public string DisplayName => "Xbox";

    /// <summary>Whether a package family is Windows' own rather than something installed.</summary>
    /// <param name="familyName">The package family name.</param>
    public static bool IsSystemPackage(string familyName)
    {
        return SystemPublishers.Any(
            prefix => familyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(CancellationToken cancellationToken)
    {
        List<DiscoveredGame> found = [];
        foreach (var package in _enumerate(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (package.Aumid.Length == 0 || IsSystemPackage(package.FamilyName))
            {
                continue;
            }

            found.Add(await DescribeAsync(package, cancellationToken).ConfigureAwait(false));
        }

        return found;
    }

    private async Task<DiscoveredGame> DescribeAsync(
        InstalledPackage package, CancellationToken cancellationToken)
    {
        List<string> notes = [];
        var manifest = Read(package, "AppxManifest.xml");
        var configText = Read(package, MicrosoftGameConfig.FileName);
        var config = configText is null ? null : MicrosoftGameConfig.Parse(configText);

        var facts = XboxManifest.ParseAppxManifest(
            manifest, package.ApplicationId, config is { Readable: true });
        var classification = XboxRuntimeClassifier.Classify(facts);

        if (config is { Readable: true, UnrecognisedElements.Count: > 0 })
        {
            // Surfaced rather than swallowed: the GDK schema varies by version, and this is how the
            // real shape gets learned from installed titles before any rule depends on more of it.
            notes.Add($"{MicrosoftGameConfig.FileName} also carries "
                      + $"{string.Join(", ", config.UnrecognisedElements)}.");
        }

        // GDK evidence answers "is this a game?" on its own; nothing in a UWP manifest does.
        var isGame = classification.Runtime is XboxRuntime.PackagedWin32Gdk;
        var multiplayer = MultiplayerVerdict.Unknown;
        var multiplayerEvidence = "Nothing was asked about this title's multiplayer support.";

        if (_lookUp is not null)
        {
            var catalog = await _lookUp(package, cancellationToken).ConfigureAwait(false);
            if (catalog is not null)
            {
                isGame = isGame || catalog.IsGame;
                multiplayer = catalog.Multiplayer;
                multiplayerEvidence = catalog.MultiplayerEvidence;
            }
            else
            {
                multiplayerEvidence =
                    "The Store had nothing for this title, so its multiplayer support is unknown.";
            }
        }

        return new DiscoveredGame(
            Id,
            package.Aumid,
            Name(package, config),
            package.InstallPath,
            classification.Runtime,
            classification.Evidence,
            multiplayer,
            multiplayerEvidence,
            isGame,
            notes);
    }

    /// <summary>What to call this title, preferring what Windows itself shows.</summary>
    private static string Name(InstalledPackage package, MicrosoftGameFacts? config)
    {
        if (package.DisplayName is { Length: > 0 } shown
            && !shown.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            return shown;
        }

        if (config is { Readable: true, DisplayName.Length: > 0 })
        {
            return config.DisplayName;
        }

        // The family name's publisher half is a poor name but a truthful one, and better than an
        // ms-resource token in somebody's Steam library.
        var separator = package.FamilyName.IndexOf('_');
        return separator > 0 ? package.FamilyName[..separator] : package.FamilyName;
    }

    private string? Read(InstalledPackage package, string fileName)
    {
        if (package.InstallPath.Length == 0)
        {
            return null;
        }

        try
        {
            return _readFile(Path.Combine(package.InstallPath, fileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // WindowsApps is ACL'd, so an unreadable package root is an ordinary outcome and means
            // only that this piece of evidence is unavailable.
            return null;
        }
    }
}
