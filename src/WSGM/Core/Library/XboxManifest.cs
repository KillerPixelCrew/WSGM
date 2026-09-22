using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace WSGM.Core;

/// <summary>Reads what a package's own manifest and game config say.</summary>
/// <remarks>
///     <para>
///         Pure parsing over text, so the classification rules are testable against fixtures rather
///         than against whatever happens to be installed.
///     </para>
///     <para>
///         Elements are matched by local name. Appx manifest namespaces are versioned and a title
///         built against a newer SDK declares the same elements under a different namespace, so
///         matching the namespace would silently stop recognising newer games.
///     </para>
/// </remarks>
public static class XboxManifest
{
    /// <summary>The largest manifest this will parse. Anything larger is not a package manifest.</summary>
    public const int MaximumBytes = 1024 * 1024;

    /// <summary>Reads an <c>AppxManifest.xml</c>.</summary>
    /// <param name="xml">The manifest text.</param>
    /// <param name="applicationId">The application to describe, or null for the only one.</param>
    /// <param name="hasGameConfig">Whether the install root carries MicrosoftGame.config.</param>
    /// <returns>What the manifest says, with <c>ManifestReadable</c> false when it could not be read.</returns>
    public static XboxPackageFacts ParseAppxManifest(
        string? xml, string? applicationId = null, bool hasGameConfig = false)
    {
        var unreadable = new XboxPackageFacts("", "", "", false, 0, [], hasGameConfig, false);
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > MaximumBytes)
        {
            return unreadable;
        }

        XDocument document;
        try
        {
            // DTD processing off and no resolver: a manifest is a file from a package, and an
            // external entity in one must not become a read of something else on this machine.
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

        var applications = document.Root?.Elements()
            .Where(element => element.Name.LocalName == "Applications")
            .SelectMany(element => element.Elements().Where(child => child.Name.LocalName == "Application"))
            .ToList() ?? [];

        var application = applicationId is { Length: > 0 }
            ? applications.FirstOrDefault(candidate =>
                string.Equals((string?)candidate.Attribute("Id"), applicationId, StringComparison.Ordinal))
            : applications.FirstOrDefault();

        // Asked for an application this package does not declare. Describing a different one would
        // classify the wrong thing.
        if (application is null)
        {
            return new XboxPackageFacts("", "", "", false, applications.Count, [], hasGameConfig);
        }

        var capabilities = document.Root?.Elements()
            .Where(element => element.Name.LocalName == "Capabilities")
            .SelectMany(element => element.Elements())
            .Select(element => (string?)element.Attribute("Name") ?? string.Empty)
            .ToList() ?? [];

        var dependencies = document.Root?.Elements()
            .Where(element => element.Name.LocalName == "Dependencies")
            .SelectMany(element => element.Elements().Where(child => child.Name.LocalName == "PackageDependency"))
            .Select(element => (string?)element.Attribute("Name") ?? string.Empty)
            .Where(name => name.Length > 0)
            .ToList() ?? [];

        return new XboxPackageFacts(
            (string?)application.Attribute("Id") ?? string.Empty,
            (string?)application.Attribute("EntryPoint") ?? string.Empty,
            // The manifest may spell the path either way; only the file name is classified on.
            Path.GetFileName(((string?)application.Attribute("Executable") ?? string.Empty)
                .Replace('/', '\\')),
            capabilities.Any(name => name.Equals("runFullTrust", StringComparison.OrdinalIgnoreCase)),
            applications.Count,
            dependencies,
            hasGameConfig);
    }

    /// <summary>The display name a manifest declares, or empty when it has none WSGM can use.</summary>
    /// <param name="xml">The manifest text.</param>
    /// <remarks>
    ///     An <c>ms-resource:</c> value is an indirection into the package's own resources, not a
    ///     name. Showing one literally would put "ms-resource:AppDisplayName" in the user's Steam
    ///     library, so it is reported as absent and a better source is used instead.
    /// </remarks>
    public static string ParseDisplayName(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > MaximumBytes)
        {
            return string.Empty;
        }

        try
        {
            using StringReader text = new(xml);
            using var reader = XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            var document = XDocument.Load(reader);
            var candidates = document.Root?.Elements()
                                 .Where(element => element.Name.LocalName is "Properties" or "Applications")
                                 .SelectMany(element => element.DescendantsAndSelf())
                                 .Where(element => element.Name.LocalName == "DisplayName")
                                 .Select(element => element.Value)
                                 .Concat(document.Root.Descendants()
                                     .Where(element => element.Name.LocalName == "VisualElements")
                                     .Select(element => (string?)element.Attribute("DisplayName") ?? string.Empty))
                             ?? [];

            foreach (var candidate in candidates)
            {
                var name = candidate.Trim();
                if (name.Length > 0 && !name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException or InvalidOperationException)
        {
            return string.Empty;
        }

        return string.Empty;
    }
}
