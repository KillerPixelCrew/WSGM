using System;
using System.Linq;

namespace WSGM.Core;

/// <summary>Shared emulator and core labels and filters for both management surfaces.</summary>
internal static class EmulatorPresentation
{
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
        return RomProfiles.ForInstallations([]).FirstOrDefault(system => system.Id == id)?.Name ?? id;
    }

    internal static string SystemGroup(string id)
    {
        return id switch
        {
            "psx" or "ps2" or "ps3" or "psp" => "Sony",
            "nes" or "snes" or "gb" or "gbc" or "gba" or "n64" or "nds" or "gamecube" or "wii"
                or "switch" => "Nintendo",
            "megadrive" or "mastersystem" or "gamegear" or "saturn" or "dreamcast" => "Sega",
            _ => "Other systems"
        };
    }

    internal static bool Matches(string search, params string[] values)
    {
        return values.Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase));
    }
}
