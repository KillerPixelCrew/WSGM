using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Builds the emulator page both surfaces draw: the installed cards and their status, the catalogue, the pending
///     updates, the systems with their library usage and the BIOS checklist, all from state already held.
/// </summary>
internal static class EmulatorPageProjection
{
    /// <summary>Projects the manager's state once for the Overlay and the Steam page.</summary>
    /// <param name="snapshot">The catalogue, installations, offers and preferences, without progress.</param>
    /// <param name="systems">The importer's systems, including those installed cores add.</param>
    /// <param name="rom">The compatible installations and cores per system.</param>
    /// <param name="bios">The last BIOS check.</param>
    /// <param name="usage">The Game Library's counts and shortcut ids.</param>
    /// <param name="setup">Each installation's setup checklist, read when its installation last changed.</param>
    /// <param name="coreStatus">Each installed core's missing files.</param>
    /// <param name="updateAll">The last Update all run, or null.</param>
    /// <param name="shortcutStatus">The shortcut reconciler's pending message, or empty.</param>
    /// <param name="architecture">The host architecture.</param>
    /// <returns>The page state.</returns>
    internal static EmulatorPageState Create(EmulatorSnapshot snapshot, IReadOnlyList<RomSystemProfile> systems,
        RomEmulatorState rom, EmulatorBiosState bios, EmulatorLibraryUsage usage,
        IReadOnlyDictionary<string, EmulatorSetupItem[]> setup, EmulatorCoreStatus[] coreStatus,
        EmulatorUpdateAllState? updateAll, string shortcutStatus, string architecture)
    {
        var updates = PendingUpdates(snapshot);
        var catalog = Catalog(snapshot);
        return new EmulatorPageState(snapshot, systems, rom.Choices, usage.Dependencies, architecture)
        {
            Bios = bios,
            ShortcutStatus = shortcutStatus,
            CoreStatus = coreStatus,
            Installed =
            [
                .. snapshot.Installations.Select(item => Installed(snapshot, item,
                    setup.GetValueOrDefault(item.Id), usage.ShortcutAppIds.GetValueOrDefault(item.Id)))
            ],
            Catalog = catalog,
            Families =
            [
                .. EmulatorPresentation.Families.Where(family =>
                    catalog.Any(item => item.Families.Contains(family, StringComparer.Ordinal)))
            ],
            Updates = updates,
            UpdatesSummary = UpdatesSummary(updates),
            UpdateAll = updateAll,
            Systems = Systems(snapshot, systems, usage.Systems),
            BiosSummary = Bios(bios)
        };
    }

    /// <summary>Reads an installation's setup checklist from its native paths; local file I/O only.</summary>
    /// <param name="installed">The installation.</param>
    /// <returns>Each declared prerequisite, then its required runtimes when it has any.</returns>
    internal static EmulatorSetupItem[] ReadSetup(EmulatorInstallation installed)
    {
        List<EmulatorSetupItem> items = [];
        foreach (var rule in installed.DataPolicy.Prerequisites)
        {
            var ready = false;
            var path = "";
            try
            {
                path = EmulatorStorage.PrerequisitePath(installed, rule);
                ready = EmulatorStorage.PrerequisitePresent(installed, rule);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                           or NotSupportedException)
            {
                // An unreadable location is reported as missing, with what to supply.
            }

            items.Add(new EmulatorSetupItem(rule.Kind, rule.Name, ready,
                ready ? "Found in " + path : rule.Description, rule.Required)
            {
                Extensions = rule.Extensions, AllowDirectory = rule.AllowDirectory
            });
        }

        var runtimes = EmulatorStorage.MissingRuntimePrerequisites(installed);
        if (installed.DataPolicy.RequiresVisualCpp || runtimes.Length > 0)
        {
            var name = installed.DataPolicy.RequiresVisualCpp && installed.DefinitionId != "model2"
                ? "Visual C++ runtime"
                : "Microsoft runtimes";
            items.Add(new EmulatorSetupItem("runtime", name, runtimes.Length == 0,
                runtimes.Length == 0 ? "Installed" : string.Join(" ", runtimes), true));
        }

        return [.. items];
    }

    /// <summary>The installed emulators whose channel offers a newer release that was not skipped.</summary>
    internal static EmulatorPendingUpdate[] PendingUpdates(EmulatorSnapshot snapshot)
    {
        return
        [
            .. snapshot.Installations.Where(item => EmulatorPresentation.HasUpdate(snapshot, item))
                .Select(item => new EmulatorPendingUpdate(item.Id, item.Name,
                    EmulatorPresentation.Offer(snapshot, item)!.Version))
        ];
    }

    /// <summary>"2 updates ready: RetroArch 1.22.0 and Cemu 2.7".</summary>
    internal static string UpdatesSummary(IReadOnlyList<EmulatorPendingUpdate> updates)
    {
        return updates.Count == 0
            ? ""
            : (updates.Count == 1 ? "1 update ready: " : updates.Count + " updates ready: ")
              + EmulatorPresentation.JoinAnd([.. updates.Select(update => update.Name + " " + update.Version)]);
    }

    /// <summary>One installed emulator's card: its lines, its status chips and its setup checklist.</summary>
    internal static EmulatorListItem Installed(EmulatorSnapshot snapshot, EmulatorInstallation item,
        EmulatorSetupItem[]? setup, uint appId)
    {
        var update = EmulatorPresentation.HasUpdate(snapshot, item);
        var offer = EmulatorPresentation.Offer(snapshot, item);
        setup ??= [];
        var missing = setup.Where(entry => entry is { Required: true, Ready: false }).ToArray();
        var needsSetup = missing.Length > 0 || (setup.Length == 0 && item.MissingRequirements.Length > 0);
        List<EmulatorStatusChip> chips = [];
        if (update)
        {
            chips.Add(new EmulatorStatusChip("update", "Update to " + offer!.Version, "action"));
        }

        if (needsSetup)
        {
            chips.Add(new EmulatorStatusChip("setup", SetupChip(missing), "warn"));
        }

        if (!item.Managed)
        {
            chips.Add(new EmulatorStatusChip("external", "External", ""));
        }
        else if (chips.Count == 0)
        {
            chips.Add(offer is null || (offer.ReleaseId.Length == 0 && offer.Error.Length == 0)
                ? new EmulatorStatusChip("notChecked", "Not checked", "")
                : offer.Error.Length > 0
                    ? new EmulatorStatusChip("checkFailed", "Check failed", "bad")
                    : offer.ReleaseId == item.IgnoredReleaseId
                        ? new EmulatorStatusChip("skipped", offer.Version + " skipped", "")
                        : new EmulatorStatusChip("upToDate", "Up to date", "good"));
        }

        return new EmulatorListItem(item.Id, EmulatorPresentation.Detail(item),
            EmulatorPresentation.Badge(snapshot, item), update)
        {
            Name = item.Name,
            VersionLine = (item.Version.Length > 0 ? item.Version : "Version unknown") + " · "
                + (item.Managed ? Capitalize(item.Channel) : "your own installation"),
            Summary = item.Cores.Length > 0
                ? EmulatorPresentation.Count(item.Cores.Length, "core") + " · " + EmulatorPresentation.Count(
                    item.Cores.SelectMany(core => core.Systems).Select(EmulatorStorage.NormalizeSystemId)
                        .Distinct(StringComparer.Ordinal).Count(), "system")
                : string.Join(" · ", item.Systems.Select(EmulatorPresentation.SystemName)),
            UpdateVersion = update ? offer!.Version : "",
            Chips = [.. chips],
            Setup = setup,
            SetupLabel = SetupLabel(setup, missing, needsSetup),
            AppId = appId
        };
    }

    private static string SetupChip(EmulatorSetupItem[] missing)
    {
        var kinds = missing.Select(entry => entry.Kind).Distinct(StringComparer.Ordinal).ToArray();
        return kinds.Length != 1
            ? "Needs setup"
            : kinds[0] switch
            {
                "bios" => "BIOS missing",
                "firmware" => "Firmware missing",
                "keys" => "Keys missing",
                "runtime" => "Runtime missing",
                _ => "Needs setup"
            };
    }

    private static string SetupLabel(EmulatorSetupItem[] setup, EmulatorSetupItem[] missing, bool needsSetup)
    {
        if (missing.Length > 0)
        {
            return missing.All(entry => entry.Kind != "runtime")
                ? EmulatorPresentation.Count(missing.Length, "file") + " missing"
                : EmulatorPresentation.Count(missing.Length, "item") + " missing";
        }

        return needsSetup ? "Needs setup" : setup.Length > 0 ? "Ready" : "";
    }

    /// <summary>Every supported emulator for Get emulators, sorted by name.</summary>
    internal static EmulatorCatalogItem[] Catalog(EmulatorSnapshot snapshot)
    {
        return
        [
            .. snapshot.Definitions.OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
                .Select(definition =>
                {
                    var channel = definition.Channels.FirstOrDefault() ?? "";
                    var offer = snapshot.Offers.FirstOrDefault(item => item.DefinitionId == definition.Id
                                                                       && item.Channel == channel
                                                                       && item.Error.Length == 0);
                    var installed = snapshot.Installations.FirstOrDefault(item =>
                        item.DefinitionId == definition.Id);
                    return new EmulatorCatalogItem(definition.Id, definition.Name,
                        string.Join(" · ", definition.Systems.Select(EmulatorPresentation.SystemName)),
                        EmulatorPresentation.FamiliesOf(definition.Systems), offer?.Version ?? "",
                        EmulatorPresentation.Requirement(definition.Prerequisites),
                        installed is null ? "" : installed.Version.Length > 0 ? installed.Version : "installed")
                    {
                        SystemIds = [.. definition.Systems.Select(EmulatorStorage.NormalizeSystemId)]
                    };
                })
        ];
    }

    /// <summary>
    ///     Every system with its library usage and whether an installed emulator runs it: a standalone emulator for the
    ///     system, or a frontend with a core whose information names it.
    /// </summary>
    internal static EmulatorSystemRow[] Systems(EmulatorSnapshot snapshot, IReadOnlyList<RomSystemProfile> systems,
        IReadOnlyList<EmulatorSystemUsage> usage)
    {
        var counted = usage.GroupBy(item => EmulatorStorage.NormalizeSystemId(item.SystemId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        return
        [
            .. systems.Select(system =>
            {
                var id = EmulatorStorage.NormalizeSystemId(system.Id);
                var used = counted.GetValueOrDefault(id);
                var sources = used?.Sources ?? 0;
                var titles = used is null ? 0 : used.Titles;
                var inLibrary = sources > 0 || titles > 0;
                var runnable = snapshot.Installations.Any(installation => installation.DataPolicy.HasCores
                    ? installation.Cores.Any(core => core.Systems.Any(named =>
                        EmulatorStorage.NormalizeSystemId(named) == id))
                    : EmulatorStorage.SupportsSystem(installation, id));
                var needsEmulator = inLibrary && !runnable;
                return new EmulatorSystemRow(system.Id, system.Name, EmulatorPresentation.SystemGroup(id), sources,
                    titles, SystemSummary(sources, titles, needsEmulator), inLibrary, needsEmulator);
            })
        ];
    }

    /// <summary>"212 games from 1 source", "9 games have no emulator yet" or "No games yet".</summary>
    internal static string SystemSummary(int sources, int? titles, bool needsEmulator)
    {
        if (needsEmulator)
        {
            return titles > 0
                ? EmulatorPresentation.Count(titles.Value, "game") + (titles == 1 ? " has" : " have")
                                                                   + " no emulator yet"
                : EmulatorPresentation.Count(sources, "source") + (sources == 1 ? " has" : " have")
                                                                + " no emulator yet";
        }

        return titles switch
        {
            > 0 when sources > 0 => EmulatorPresentation.Count(titles.Value, "game") + " from "
                + EmulatorPresentation.Count(sources, "source"),
            > 0 => EmulatorPresentation.Count(titles.Value, "game"),
            null when sources > 0 => EmulatorPresentation.Count(sources, "source")
                                     + " · imported games could not be counted",
            _ when sources > 0 => "No games yet from " + EmulatorPresentation.Count(sources, "source"),
            _ => "No games yet"
        };
    }

    /// <summary>The BIOS checklist per system and the files found over the systems the installed emulators use.</summary>
    internal static EmulatorBiosSummary Bios(EmulatorBiosState bios)
    {
        var systems = bios.Systems.Select(system => new EmulatorBiosChecklist(system.Id, system.Name,
                string.Join(" · ", system.Emulators),
                system.Files.Length > 0 && system.Files.All(file => file.Optional),
                BiosItems(system)))
            .OrderBy(system => bios.Systems.First(item => item.Id == system.SystemId).Emulators.Length == 0)
            .ToArray();
        var needed = systems.Where(system => system.UsedBy.Length > 0)
            .SelectMany(system => system.Items).Where(item => !item.Optional).ToArray();
        var found = needed.Count(item => item.State is "verified" or "present");
        var label = !bios.Checked
            ? "Not checked yet"
            : systems.All(system => system.UsedBy.Length == 0)
                ? "No installed emulator needs files"
                : needed.Length == 0
                    ? "Nothing required"
                    : found.ToString(CultureInfo.InvariantCulture) + " of "
                                                                   + EmulatorPresentation.Count(needed.Length, "file")
                                                                   + " found";
        return new EmulatorBiosSummary(found, needed.Length, label, systems);
    }

    private static EmulatorBiosItem[] BiosItems(EmulatorBiosSystem system)
    {
        if (!system.Any)
        {
            return [.. system.Files.Select(Item)];
        }

        // Regional alternatives: one present file satisfies the system, so the others are not missing.
        var supplied = system.Files.Where(file => file.Status != "Missing").Select(Item).ToList();
        if (!supplied.Any(item => item.State is "verified" or "present") && system.Files.Length > 0)
        {
            supplied.Add(new EmulatorBiosItem("Any " + system.Name + " BIOS (for example "
                                              + system.Files[0].Path + ")", "missing", "Missing",
                system.Files.All(file => file.Optional)));
        }

        return [.. supplied];
    }

    private static EmulatorBiosItem Item(EmulatorBiosFile file)
    {
        return file.Status switch
        {
            "Verified" => new EmulatorBiosItem(file.Path, "verified", "Verified", file.Optional),
            "Present" => new EmulatorBiosItem(file.Path, "present", "Present, no checksum to verify", file.Optional),
            "Wrong file" => new EmulatorBiosItem(file.Path, "wrongFile", "Wrong file", file.Optional),
            _ => new EmulatorBiosItem(file.Path, "missing", file.Optional ? "Optional, not added" : "Missing",
                file.Optional)
        };
    }

    private static string Capitalize(string value)
    {
        return value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
    }
}
