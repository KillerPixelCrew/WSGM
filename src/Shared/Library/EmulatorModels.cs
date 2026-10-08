// Shared between WSGM and WSGM.PackagedLaunch (linked as a source file).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace WSGM.Core;

/// <summary>A reviewed emulator definition, independent of its installed version.</summary>
public sealed record EmulatorDefinition
{
    /// <summary>Reviewed data, runtime and core requirements shared by all surfaces.</summary>
    public EmulatorDataPolicy DataPolicy { get; init; } = new();

    /// <summary>Stable identifier within its owning catalogue.</summary>
    public string Id { get; init; } = "";

    /// <summary>User-facing display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Supported canonical ROM system identifiers.</summary>
    public string[] Systems { get; init; } = [];

    /// <summary>Available upstream release channels.</summary>
    public string[] Channels { get; init; } = [];

    /// <summary>Configured release source identity.</summary>
    public string Source { get; init; } = "";

    /// <summary>Local setup actions supported by this reviewed emulator.</summary>
    public EmulatorPrerequisite[] Prerequisites { get; init; } = [];
}

/// <summary>One actually installed core package, including visible metadata gaps.</summary>
public sealed record EmulatorCore
{
    /// <summary>Stable identifier within its owning catalogue.</summary>
    public string Id { get; init; } = "";

    /// <summary>User-facing display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Supported canonical ROM system identifiers.</summary>
    public string[] Systems { get; init; } = [];

    /// <summary>Supported content or prerequisite file suffixes.</summary>
    public string[] Extensions { get; init; } = [];

    /// <summary>Resolved local file path.</summary>
    public string Path { get; init; } = "";

    /// <summary>Exact upstream asset URL recorded for provenance.</summary>
    public string SourceUrl { get; init; } = "";

    /// <summary>Locally computed SHA-256 receipt; distinct from publisher verification.</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>Whether the installed core lacks upstream core-info metadata.</summary>
    public bool MetadataMissing { get; init; }

    /// <summary>Local firmware paths declared by this core's metadata.</summary>
    public string[] RequiredFiles { get; init; } = [];
}

/// <summary>The stable identity and active program/data paths consumed by the managed launcher.</summary>
public sealed record EmulatorInstallation
{
    /// <summary>Original archive asset name used to locate the exact retained repair package.</summary>
    public string PackageName { get; init; } = "";

    /// <summary>The installed definition's reviewed data and runtime contract.</summary>
    public EmulatorDataPolicy DataPolicy { get; init; } = new();

    /// <summary>Retained exact payloads used to repair this installed version.</summary>
    public string PackageCachePath { get; init; } = "";

    /// <summary>Recorded archive hashes for the retained repair payloads.</summary>
    public Dictionary<string, string> PackageHashes { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Core catalogue identity independent of the frontend release and skip policy.</summary>
    public string CoreCatalogueRevision { get; init; } = "";

    /// <summary>Stable identifier within its owning catalogue.</summary>
    public string Id { get; init; } = "";

    /// <summary>DefinitionStable identifier within its owning catalogue.</summary>
    public string DefinitionId { get; init; } = "";

    /// <summary>User-facing display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Whether WSGM owns this program installation and its updates.</summary>
    public bool Managed { get; init; }

    /// <summary>Selected upstream release channel.</summary>
    public string Channel { get; init; } = "";

    /// <summary>Windows architecture selected for this installation.</summary>
    public string Architecture { get; init; } = "x64";

    /// <summary>Upstream display version.</summary>
    public string Version { get; init; } = "";

    /// <summary>ReleaseStable identifier within its owning catalogue.</summary>
    public string ReleaseId { get; init; } = "";

    /// <summary>Known program installation root.</summary>
    public string Root { get; init; } = "";

    /// <summary>Active executable resolved from the installed version.</summary>
    public string ExecutablePath { get; init; } = "";

    /// <summary>Persistent emulator configuration and save-data root.</summary>
    public string DataPath { get; init; } = "";

    /// <summary>Whether this data root is WSGM-owned and may be explicitly removed.</summary>
    public bool OwnsData { get; init; }

    /// <summary>PreviousActive executable resolved from the installed version.</summary>
    public string PreviousExecutablePath { get; init; } = "";

    /// <summary>Exact upstream asset URL recorded for provenance.</summary>
    public string SourceUrl { get; init; } = "";

    /// <summary>Configured release source identity.</summary>
    public string Source { get; init; } = "";

    /// <summary>Locally computed SHA-256 receipt; distinct from publisher verification.</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>ExpectedLocally computed SHA-256 receipt; distinct from publisher verification.</summary>
    public string ExpectedSha256 { get; init; } = "";

    /// <summary>Human-readable checksum verification provenance.</summary>
    public string Integrity { get; init; } = "";

    /// <summary>IgnoredReleaseStable identifier within its owning catalogue.</summary>
    public string IgnoredReleaseId { get; init; } = "";

    /// <summary>Supported canonical ROM system identifiers.</summary>
    public string[] Systems { get; init; } = [];

    /// <summary>Typed launch tokens; paths are resolved immediately before starting.</summary>
    public string[] LaunchArguments { get; init; } = [];

    /// <summary>Child-only environment overrides with declared path tokens.</summary>
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Every actually installed core package, including metadata gaps.</summary>
    public EmulatorCore[] Cores { get; init; } = [];

    /// <summary>Observed missing or unconfigured local prerequisites.</summary>
    public string[] MissingRequirements { get; init; } = [];

    /// <summary>ConfiguredLocal setup actions supported by this reviewed emulator.</summary>
    public Dictionary<string, string> ConfiguredPrerequisites { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>EmulatorPrerequisite used by emulator management and fresh managed launch resolution.</summary>
public sealed record EmulatorPrerequisite
{
    /// <summary>Directory relative to the persistent data root.</summary>
    public string Destination { get; init; } = "";

    /// <summary>Whether launch requires the declared data to be present.</summary>
    public bool Required { get; init; }

    /// <summary>Filename suffixes whose presence satisfies a required directory.</summary>
    public string[] RequiredExtensions { get; init; } = [];

    /// <summary>INI file relative to the data root containing a custom path.</summary>
    public string IniFile { get; init; } = "";

    /// <summary>Section containing the custom data path.</summary>
    public string IniSection { get; init; } = "";

    /// <summary>Key containing the custom data path.</summary>
    public string IniKey { get; init; } = "";

    /// <summary>Subdirectory appended to the custom native path.</summary>
    public string PathSuffix { get; init; } = "";

    /// <summary>Uses the emulator-owned installer rather than copying files.</summary>
    public bool NativeInstaller { get; init; }

    /// <summary>Specific files required beneath the prerequisite directory.</summary>
    public string[] RequiredNames { get; init; } = [];

    /// <summary>Reviewed installer arguments; {source} is the locally supplied file.</summary>
    public string[] InstallerArguments { get; init; } = [];

    /// <summary>Reviewed prerequisite action identifier.</summary>
    public string Kind { get; init; } = "";

    /// <summary>User-facing display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Explanation of the local setup operation.</summary>
    public string Description { get; init; } = "";

    /// <summary>Supported content or prerequisite file suffixes.</summary>
    public string[] Extensions { get; init; } = [];

    /// <summary>Whether this setup action accepts a local folder.</summary>
    public bool AllowDirectory { get; init; }
}

/// <summary>Declarative emulator data and prerequisite rules; contains no install scripts.</summary>
public sealed record EmulatorDataPolicy
{
    /// <summary>Whether launch selects an installed core.</summary>
    public bool HasCores { get; init; }

    /// <summary>Whether this package needs the Microsoft Visual C++ runtime.</summary>
    public bool RequiresVisualCpp { get; init; }

    /// <summary>Native data roots in precedence order, or empty for managed data.</summary>
    public string[] NativeRoots { get; init; } = [];

    /// <summary>Native roots for externally owned installs.</summary>
    public string[] ExternalRoots { get; init; } = ["{program}"];

    /// <summary>Program-relative markers selecting portable data.</summary>
    public string[] PortableMarkers { get; init; } = [];

    /// <summary>Program-relative portable data directory.</summary>
    public string PortableDirectory { get; init; } = "";

    /// <summary>Managed data-path arguments, separate from ordinary launch arguments.</summary>
    public string[] DataArguments { get; init; } = [];

    /// <summary>Managed child-only environment path overrides.</summary>
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Program-local directory used to isolate noninteractive validation.</summary>
    public string ProbeDirectory { get; init; } = "";

    /// <summary>Data-relative configuration file expanded by the config launch token.</summary>
    public string ConfigFile { get; init; } = "";

    /// <summary>Configuration keys and data/program-relative paths written when a managed version activates.</summary>
    public Dictionary<string, string> ConfigPaths { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Reviewed local setup and readiness rules.</summary>
    public EmulatorPrerequisite[] Prerequisites { get; init; } = [];
}

/// <summary>EmulatorSystemPreference used by emulator management and fresh managed launch resolution.</summary>
public sealed record EmulatorSystemPreference
{
    /// <summary>Canonical ROM system to which this preference applies.</summary>
    public string SystemId { get; init; } = "";

    /// <summary>InstallationStable identifier within its owning catalogue.</summary>
    public string InstallationId { get; init; } = "";

    /// <summary>CoreStable identifier within its owning catalogue.</summary>
    public string CoreId { get; init; } = "";
}

/// <summary>EmulatorOffer used by emulator management and fresh managed launch resolution.</summary>
public sealed record EmulatorOffer
{
    /// <summary>DefinitionStable identifier within its owning catalogue.</summary>
    public string DefinitionId { get; init; } = "";

    /// <summary>Selected upstream release channel.</summary>
    public string Channel { get; init; } = "";

    /// <summary>Windows architecture selected for this installation.</summary>
    public string Architecture { get; init; } = "x64";

    /// <summary>Upstream display version.</summary>
    public string Version { get; init; } = "";

    /// <summary>ReleaseStable identifier within its owning catalogue.</summary>
    public string ReleaseId { get; init; } = "";

    /// <summary>Upstream release notes or project page URL.</summary>
    public string NotesUrl { get; init; } = "";

    /// <summary>Release discovery failure, distinct from no update being available.</summary>
    public string Error { get; init; } = "";
}

/// <summary>EmulatorSnapshot used by emulator management and fresh managed launch resolution.</summary>
public sealed record EmulatorSnapshot : EmulatorStore
{
    /// <summary>Whether local installation receipts have been loaded successfully.</summary>
    public bool Initialized { get; init; }

    /// <summary>Detached reviewed emulator definitions.</summary>
    public EmulatorDefinition[] Definitions { get; init; } = [];

    /// <summary>Whether one manager operation owns admission.</summary>
    public bool Busy { get; init; }

    /// <summary>Current user-facing operation result or progress.</summary>
    public string Status { get; init; } = "";
}

/// <summary>EmulatorStore used by emulator management and fresh managed launch resolution.</summary>
public record EmulatorStore
{
    /// <summary>Shared EmuDeck-layout folder containing locally supplied BIOS and firmware.</summary>
    public string BiosFolder { get; init; } = "";

    /// <summary>External installation identities explicitly forgotten by the user.</summary>
    public string[] ForgottenExternalIds { get; init; } = [];

    /// <summary>Supported durable store schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Detached local installation receipts.</summary>
    public EmulatorInstallation[] Installations { get; init; } = [];

    /// <summary>Last discovered release offer per emulator/channel.</summary>
    public EmulatorOffer[] Offers { get; init; } = [];

    /// <summary>Persisted preferred emulator and core per ROM system.</summary>
    public EmulatorSystemPreference[] SystemPreferences { get; init; } = [];
}

/// <summary>EmulatorResolvedLaunch used by emulator management and fresh managed launch resolution.</summary>
/// <param name="Executable">The resolved Executable for this launch admission.</param>
/// <param name="WorkingDirectory">The resolved WorkingDirectory for this launch admission.</param>
/// <param name="Arguments">The resolved Arguments for this launch admission.</param>
/// <param name="Environment">The resolved Environment for this launch admission.</param>
public sealed record EmulatorResolvedLaunch(
    string Executable,
    string WorkingDirectory,
    string[] Arguments,
    Dictionary<string, string> Environment);

/// <summary>One scan/recheck batch's cached emulator checks; never reused for a later launch.</summary>
public sealed class EmulatorResolveContext
{
    internal Dictionary<object, Exception?> Checks { get; } = new();

    internal void Check(object key, Action validate)
    {
        if (!Checks.TryGetValue(key, out var failure))
        {
            try
            {
                validate();
                Checks[key] = null;
                return;
            }
            catch (Exception ex)
            {
                Checks[key] = ex;
                throw;
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}

/// <summary>Small read-only store/resolver shared with the launch helper. It never starts the app.</summary>
public static class EmulatorStorage
{
    /// <summary>Shared camel-case serialization options for emulator metadata and receipts.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>Maps upstream aliases to the importer's canonical system identity.</summary>
    public static string NormalizeSystemId(string systemId)
    {
        return systemId.Equals("genesis", StringComparison.OrdinalIgnoreCase)
            ? "megadrive"
            : systemId.ToLowerInvariant();
    }

    /// <summary>Checks the shared compatible-system rule used by preferences and launch resolution.</summary>
    public static bool SupportsSystem(EmulatorInstallation installation, string systemId)
    {
        return installation.DataPolicy.HasCores
               || installation.Systems.Any(system => NormalizeSystemId(system) == NormalizeSystemId(systemId));
    }

    /// <summary>Returns known compatible cores and explicitly unknown metadata entries.</summary>
    public static EmulatorCore[] CompatibleCores(EmulatorInstallation installation, string systemId)
    {
        return installation.Cores
            .Where(core => core.MetadataMissing || core.Systems.Length == 0
                                                || core.Systems.Any(system =>
                                                    NormalizeSystemId(system) == NormalizeSystemId(systemId)))
            .ToArray();
    }

    /// <summary>Checks an emulator/core selection without requiring media or firmware to be present.</summary>
    /// <param name="installation">The selected installed emulator.</param>
    /// <param name="systemId">The ROM system, including recognized aliases.</param>
    /// <param name="coreId">The selected core when the emulator requires one.</param>
    /// <returns>The compatible core, or null for a standalone emulator.</returns>
    public static EmulatorCore? ValidateSelection(EmulatorInstallation installation, string systemId, string? coreId)
    {
        if (!SupportsSystem(installation, systemId))
        {
            throw new InvalidOperationException("The selected emulator does not support this system.");
        }

        if (!installation.DataPolicy.HasCores)
        {
            return null;
        }

        return CompatibleCores(installation, systemId).SingleOrDefault(core => core.Id == coreId)
               ?? throw new InvalidOperationException("Choose an installed core supporting this ROM system.");
    }

    /// <summary>Returns the per-user durable emulator state path.</summary>
    public static string StorePath(string userRoot)
    {
        return Path.Combine(userRoot, "emulators.json");
    }

    /// <summary>Returns the per-user mutex shared by fresh launch admission and synchronous metadata activation.</summary>
    public static string GateName(string userRoot)
    {
        return @"Local\WSGM.Emulators." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(userRoot).ToUpperInvariant())))[..32];
    }

    /// <summary>Reads fresh emulator state and rejects unsupported store formats.</summary>
    public static EmulatorStore ReadStore(string userRoot)
    {
        var path = StorePath(userRoot);
        if (!File.Exists(path))
        {
            return new EmulatorStore();
        }

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var store = JsonSerializer.Deserialize<EmulatorStore>(file, JsonOptions)
                    ?? throw new InvalidDataException("The emulator state is empty.");
        if (store.SchemaVersion != 1 || store.Installations is null || store.Offers is null ||
            store.SystemPreferences is null)
        {
            throw new InvalidDataException("The emulator state version is unsupported.");
        }

        return store;
    }

    /// <summary>Reads current installation receipts for independent launch resolution.</summary>
    public static EmulatorInstallation[] ReadInstallations(string userRoot)
    {
        return ReadStore(userRoot).Installations;
    }

    /// <summary>Resolves fresh installation, core, argument and prerequisite paths without starting a process.</summary>
    public static EmulatorResolvedLaunch Resolve(string userRoot, string installationId, string systemId,
        string? coreId, string romPath, IReadOnlyList<string>? launchArguments = null,
        EmulatorResolveContext? context = null)
    {
        return Resolve(ReadStore(userRoot), installationId, systemId, coreId, romPath, launchArguments, context);
    }

    /// <summary>Resolves one title from an already validated store snapshot for bounded batch discovery.</summary>
    public static EmulatorResolvedLaunch Resolve(EmulatorStore store, string installationId, string systemId,
        string? coreId, string romPath, IReadOnlyList<string>? launchArguments = null,
        EmulatorResolveContext? context = null)
    {
        var installation = store.Installations.SingleOrDefault(item => item.Id == installationId)
                           ?? throw new InvalidOperationException(
                               "The selected emulator is not installed. Reinstall or choose another emulator.");
        var core = ValidateSelection(installation, systemId, coreId);

        void CheckInstallation()
        {
            if (!File.Exists(installation.ExecutablePath))
            {
                throw new FileNotFoundException("The selected emulator executable is missing. Use Repair.",
                    installation.ExecutablePath);
            }

            var missing = MissingPrerequisites(installation);
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    missing[0] + " Open Emulator Manager to configure or Recheck setup.");
            }
        }

        if (context is null)
        {
            CheckInstallation();
        }
        else
        {
            context.Check(installation, CheckInstallation);
        }

        var required = core?.RequiredFiles ?? [];
        if (core is not null)
        {
            required = [.. required, core.Path];
        }

        void CheckRequired()
        {
            foreach (var path in required)
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    throw new FileNotFoundException("An emulator prerequisite is missing: " + path, path);
                }
            }
        }

        if (context is null)
        {
            CheckRequired();
        }
        else if (core is not null)
        {
            context.Check(core, CheckRequired);
        }

        string Expand(string value)
        {
            return value.Replace("{rom}", romPath, StringComparison.Ordinal)
                .Replace("{core}", core?.Path ?? "", StringComparison.Ordinal)
                .Replace("{data}", installation.DataPath, StringComparison.Ordinal)
                .Replace("{config}", Path.Combine(installation.DataPath, installation.DataPolicy.ConfigFile),
                    StringComparison.Ordinal);
        }

        return new EmulatorResolvedLaunch(installation.ExecutablePath,
            Path.GetDirectoryName(installation.ExecutablePath)!,
            (launchArguments ?? installation.LaunchArguments).Select(Expand).ToArray(),
            installation.Environment.ToDictionary(pair => pair.Key, pair => Expand(pair.Value),
                StringComparer.Ordinal));
    }

    /// <summary>Checks declared native BIOS/data/runtime locations afresh without relying on cached setup state.</summary>
    public static string[] MissingPrerequisites(EmulatorInstallation installed)
    {
        var missing = new List<string>();
        foreach (var rule in installed.DataPolicy.Prerequisites.Where(rule => rule.Required))
        {
            if (!PrerequisitePresent(installed, rule))
            {
                missing.Add("Configure " + rule.Name + ". " + rule.Description);
            }
        }

        if (installed.DataPolicy.RequiresVisualCpp
            && !File.Exists(Path.Combine(Path.GetDirectoryName(installed.ExecutablePath)!, "vcruntime140_1.dll"))
            && !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "vcruntime140_1.dll")))
        {
            missing.Add("Install Microsoft's Visual C++ runtime matching the emulator architecture.");
        }

        return [.. missing];
    }

    internal static bool PrerequisitePresent(EmulatorInstallation installed, EmulatorPrerequisite rule)
    {
        var path = PrerequisitePath(installed, rule);
        return rule.RequiredNames.Length > 0
            ? rule.RequiredNames.All(name => File.Exists(Path.Combine(path, name)))
            : rule.RequiredExtensions.Length > 0
                ? Directory.Exists(path) && Directory.EnumerateFiles(path).Any(file =>
                    rule.RequiredExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                : rule.NativeInstaller
                    ? Directory.Exists(path) && Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any()
                    : Directory.Exists(path) || File.Exists(path);
    }

    /// <summary>Acquires launch/activation admission; dispose on this same thread.</summary>
    public static IDisposable? TryAcquireGate(string userRoot, TimeSpan timeout)
    {
        return NamedMutexLease.TryAcquire(GateName(userRoot), timeout);
    }

    /// <summary>Acquires cancellable admission without polling; dispose on this same thread.</summary>
    public static IDisposable AcquireGate(string userRoot, CancellationToken cancellationToken)
    {
        return NamedMutexLease.Acquire(GateName(userRoot), cancellationToken);
    }

    /// <summary>Resolves the actual native destination, respecting configured INI overrides.</summary>
    public static string PrerequisitePath(EmulatorInstallation installed, EmulatorPrerequisite rule)
    {
        var path = Path.Combine(installed.DataPath, rule.Destination);
        if (rule.IniFile.Length > 0)
        {
            var custom = IniFile.ReadValue(Path.Combine(installed.DataPath, rule.IniFile), rule.IniKey, rule.IniSection)
                ?.Trim('"');
            if (!string.IsNullOrWhiteSpace(custom))
            {
                path = Path.IsPathRooted(custom) ? custom : Path.Combine(installed.DataPath, custom);
            }
        }

        return Path.GetFullPath(Path.Combine(path, rule.PathSuffix));
    }
}
