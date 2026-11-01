using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WSGM.Core;

/// <summary>Shared emulator and core labels and filters for both management surfaces.</summary>
internal static class EmulatorPresentation
{
    /// <summary>The console families both surfaces filter and group systems by, in the order they list them.</summary>
    internal static string[] Families { get; } = ["Nintendo", "PlayStation", "Sega", "Xbox", "Arcade", "Other systems"];

    internal static EmulatorOffer? Offer(EmulatorSnapshot state, EmulatorInstallation installed)
    {
        return state.Offers.FirstOrDefault(item => item.DefinitionId == installed.DefinitionId
                                                   && item.Channel == installed.Channel
                                                   && item.Architecture == installed.Architecture);
    }

    internal static bool HasUpdate(EmulatorSnapshot state, EmulatorInstallation installed)
    {
        var offer = Offer(state, installed);
        return installed.Managed && offer is { Error.Length: 0, ReleaseId.Length: > 0 }
                                 && offer.ReleaseId != installed.ReleaseId &&
                                 offer.ReleaseId != installed.IgnoredReleaseId;
    }

    internal static string Badge(EmulatorSnapshot state, EmulatorInstallation installed)
    {
        if (!installed.Managed)
        {
            return "External";
        }

        if (HasUpdate(state, installed))
        {
            return "Update " + Offer(state, installed)!.Version;
        }

        if (installed.MissingRequirements.Length > 0)
        {
            return "Needs setup";
        }

        var offer = Offer(state, installed);
        return offer is null || (offer.ReleaseId.Length == 0 && offer.Error.Length == 0) ? "Not checked"
            : offer.Error.Length > 0 ? "Check failed"
            : offer.ReleaseId == installed.IgnoredReleaseId ? "Version skipped" : "Up to date";
    }

    internal static string Detail(EmulatorInstallation installed)
    {
        return $"{installed.Version} · {installed.Channel} · {(installed.Managed ? "Managed" : "External")}"
               + (installed.Cores.Length > 0 ? $" · {installed.Cores.Length} cores" : " · " + installed.Architecture)
               + (installed.MissingRequirements.Length > 0 ? " · Needs setup" : "");
    }

    internal static string SystemName(string id)
    {
        return RomProfiles.All.FirstOrDefault(system => system.Id == id)?.Name ?? id;
    }

    /// <summary>The console family a canonical system belongs to.</summary>
    /// <param name="id">The system identity, aliases included.</param>
    /// <returns>One of <see cref="Families" />.</returns>
    internal static string SystemGroup(string id)
    {
        return EmulatorStorage.NormalizeSystemId(id) switch
        {
            "psx" or "ps2" or "ps3" or "ps4" or "psp" or "psvita" => "PlayStation",
            "nes" or "snes" or "gb" or "gbc" or "gba" or "n64" or "nds" or "3ds" or "gamecube" or "wii" or "wiiu"
                or "switch" => "Nintendo",
            "megadrive" or "mastersystem" or "gamegear" or "sega32x" or "pico" or "saturn" or "dreamcast" => "Sega",
            "xbox" or "xbox360" => "Xbox",
            "arcade" or "naomi" or "atomiswave" or "model2" or "model3" => "Arcade",
            _ => "Other systems"
        };
    }

    /// <summary>The families a set of systems covers, in <see cref="Families" /> order.</summary>
    internal static string[] FamiliesOf(string[] systems)
    {
        var covered = systems.Select(SystemGroup).ToHashSet(StringComparer.Ordinal);
        return [.. Families.Where(covered.Contains)];
    }

    /// <summary>The prerequisites an emulator needs before its games start, read from its reviewed definition.</summary>
    /// <param name="prerequisites">The definition's local setup rules.</param>
    /// <returns>"Needs after install: …", or empty when nothing is required.</returns>
    internal static string Requirement(EmulatorPrerequisite[] prerequisites)
    {
        var names = prerequisites.Where(rule => rule.Required).Select(rule => rule.Name).ToArray();
        return names.Length == 0 ? "" : "Needs after install: " + JoinAnd(names);
    }

    /// <summary>Joins names as "A", "A and B" or "A, B and C".</summary>
    internal static string JoinAnd(IReadOnlyList<string> names)
    {
        return names.Count switch
        {
            0 => "",
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1]
        };
    }

    /// <summary>Counts a noun: "1 source", "2 sources".</summary>
    internal static string Count(int count, string noun, string plural = "")
    {
        return count.ToString(CultureInfo.InvariantCulture) + " " +
               (count == 1 ? noun : plural.Length > 0 ? plural : noun + "s");
    }

    internal static bool Matches(string search, params string[] values)
    {
        return values.Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase));
    }
}
