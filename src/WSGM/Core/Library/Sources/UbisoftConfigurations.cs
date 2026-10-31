using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WSGM.Core;

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
