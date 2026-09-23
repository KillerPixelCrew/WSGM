using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace WSGM.Core;

/// <summary>What a GDK title's <c>MicrosoftGame.config</c> says.</summary>
/// <param name="Executables">The executables it names, by file name.</param>
/// <param name="DisplayName">Its shell display name, or empty.</param>
/// <param name="StoreId">Its Store id, or empty.</param>
/// <param name="TitleId">Its Xbox title id, or empty.</param>
/// <param name="UnrecognisedElements">
///     Top-level elements this parser does not model, in document order.
/// </param>
/// <param name="Readable">Whether the file could be read and parsed at all.</param>
public sealed record MicrosoftGameFacts(
    IReadOnlyList<string> Executables,
    string DisplayName,
    string StoreId,
    string TitleId,
    IReadOnlyList<string> UnrecognisedElements,
    bool Readable = true);

/// <summary>Reads a GDK title's game configuration.</summary>
/// <remarks>
///     <para>
///         The presence of this file is the clearest signal that a full-trust package is a GDK
///         title, which is the shape the packaged Win32 launch route was demonstrated on. It also
///         names the real game executable, which the launch helper eventually hands off to.
///     </para>
///     <para>
///         <see cref="MicrosoftGameFacts.UnrecognisedElements" /> is deliberate. The schema varies
///         across GDK versions and this parser models only what is needed today, so the import
///         preview can surface what a real installed title actually carries. That is how the schema
///         gets learned from evidence rather than assumed, before any rule depends on more of it.
///     </para>
/// </remarks>
public static class MicrosoftGameConfig
{
    /// <summary>The file name, in the package root.</summary>
    public const string FileName = "MicrosoftGame.config";

    /// <summary>The largest config this will parse.</summary>
    public const int MaximumBytes = 1024 * 1024;

    /// <summary>The top-level elements this parser models.</summary>
    private static readonly HashSet<string> Modelled = new(StringComparer.OrdinalIgnoreCase)
    {
        "Identity", "ExecutableList", "ShellVisuals", "StoreId", "TitleId", "MSAAppId",
        "ConfigVersion", "Game"
    };

    /// <summary>Reads a game configuration.</summary>
    /// <param name="xml">The file's text.</param>
    /// <returns>What it says, with <c>Readable</c> false when it could not be parsed.</returns>
    public static MicrosoftGameFacts Parse(string? xml)
    {
        var unreadable = new MicrosoftGameFacts([], "", "", "", [], false);
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > MaximumBytes)
        {
            return unreadable;
        }

        XDocument document;
        try
        {
            using StringReader text = new(xml);
            using var reader = XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true
            });
            document = XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is XmlException or IOException or InvalidOperationException)
        {
            return unreadable;
        }

        var root = document.Root;
        if (root is null)
        {
            return unreadable;
        }

        var executables = root.Descendants()
            .Where(element => element.Name.LocalName == "Executable")
            .Select(element => Path.GetFileName(((string?)element.Attribute("Name") ?? string.Empty)
                .Replace('/', '\\')))
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var display = root.Descendants()
            .Where(element => element.Name.LocalName == "ShellVisuals")
            .Select(element => (string?)element.Attribute("DefaultDisplayName") ?? string.Empty)
            .FirstOrDefault(name => name.Length > 0) ?? string.Empty;

        var unrecognised = root.Elements()
            .Select(element => element.Name.LocalName)
            .Where(name => !Modelled.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MicrosoftGameFacts(
            executables,
            display.Trim(),
            Value(root, "StoreId"),
            Value(root, "TitleId"),
            unrecognised);
    }

    private static string Value(XElement root, string name)
    {
        return root.Elements().FirstOrDefault(element =>
                   element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim()
               ?? string.Empty;
    }
}
