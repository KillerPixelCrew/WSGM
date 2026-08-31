using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace WSGM.Core;

/// <summary>Reads installed extensions from a directory and decides which may contribute.</summary>
/// <remarks>
/// An extension is a module discovered from a package instead of compiled in: same patch lifecycle,
/// same ownership rules, same clean removal. The difference is that its identity cannot be trusted,
/// so everything an installed extension declares is checked here before any of its code is injected.
/// <para>
/// This reads and validates only. It loads no assemblies and executes nothing — the script it
/// returns is text until the asset that carries it is injected, and that injection goes through the
/// same probe/apply/verify/remove path every built-in surface uses. An extension that fails any
/// check is still reported rather than skipped silently, because "my extension does nothing" with
/// no reason anywhere is the failure this whole subsystem exists to avoid.
/// </para>
/// <para>
/// <b>This is not a sandbox.</b> Injected script runs with the same reach as the host's own gates —
/// it can read and change anything in Steam's front-end. The checks here are about identity and
/// collision, so one extension cannot impersonate another or quietly claim its patches. Treat
/// installing an extension as running its code, because that is what it is.
/// </para>
/// </remarks>
public static class SteamUiExtensionHost
{
    /// <summary>The extension API version this host implements.</summary>
    public const int ApiVersion = 1;

    /// <summary>The manifest file every extension package must contain.</summary>
    public const string ManifestFileName = "extension.wsgm.json";

    /// <summary>Largest script accepted from one extension.</summary>
    /// <remarks>The whole injected asset is evaluated in one CDP call, so an unbounded script is a
    /// way to make that call fail for every surface, not just the extension's own.</remarks>
    public const int MaximumScriptCharacters = 256 * 1024;

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    /// <summary>Examines every package directory and reports what each one is.</summary>
    /// <param name="root">Directory holding one subdirectory per installed extension.</param>
    /// <returns>Every extension found, loaded or rejected, ordered by id. An absent or unreadable
    /// root is an empty list rather than an error: no extensions installed is the normal case.</returns>
    public static IReadOnlyList<SteamUiExtension> Discover(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string[] directories;
        try
        {
            directories = Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SteamUiLog.Change(
                "steam.ui.extensions.root",
                $"Steam UI extensions could not be listed: {ex.Message}",
                warning: true);
            return [];
        }

        Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
        List<SteamUiExtension> examined = [];
        HashSet<string> claimedIds = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> claimedPatches = new(StringComparer.Ordinal);

        foreach (string directory in directories)
        {
            SteamUiExtension extension = Examine(directory);
            if (extension.Loaded)
            {
                extension = ResolveConflicts(extension, claimedIds, claimedPatches);
            }
            examined.Add(extension);
        }

        foreach (SteamUiExtension extension in examined)
        {
            SteamUiLog.Change(
                "steam.ui.extension." + extension.Id,
                extension.Loaded
                    ? $"Steam UI extension {extension.Id} loaded."
                    : $"Steam UI extension {extension.Id} refused ({extension.Rejection}): "
                        + (extension.Detail ?? "no detail"),
                warning: !extension.Loaded);
        }

        return examined;
    }

    private static SteamUiExtension ResolveConflicts(
        SteamUiExtension extension,
        HashSet<string> claimedIds,
        HashSet<string> claimedPatches)
    {
        // First one wins, and the second is named. Silently preferring either would make an
        // extension that stopped working impossible to explain.
        if (!claimedIds.Add(extension.Id))
        {
            return Reject(
                extension.Id,
                SteamUiExtensionRejection.Conflict,
                "another installed extension already uses this id");
        }

        foreach (string patch in extension.Manifest!.Patches)
        {
            if (!claimedPatches.Add(patch))
            {
                return Reject(
                    extension.Id,
                    SteamUiExtensionRejection.Conflict,
                    $"patch '{patch}' is already claimed by another extension");
            }
        }

        return extension;
    }

    private static SteamUiExtension Examine(string directory)
    {
        string fallbackId = Path.GetFileName(directory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string manifestPath = Path.Combine(directory, ManifestFileName);

        SteamUiExtensionManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SteamUiExtensionManifest>(
                File.ReadAllText(manifestPath), ManifestOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or NotSupportedException)
        {
            return Reject(fallbackId, SteamUiExtensionRejection.UnreadableManifest, ex.Message);
        }

        if (manifest is null
            || !IsSafeIdentifier(manifest.Id)
            || string.IsNullOrWhiteSpace(manifest.Name)
            || string.IsNullOrWhiteSpace(manifest.Version)
            || string.IsNullOrWhiteSpace(manifest.Script))
        {
            return Reject(
                fallbackId,
                SteamUiExtensionRejection.InvalidManifest,
                "id, name, version and script are required, and id must be a safe identifier");
        }

        if (manifest.ApiVersion != ApiVersion)
        {
            return Reject(
                manifest.Id,
                SteamUiExtensionRejection.ApiVersionMismatch,
                $"built against extension API {manifest.ApiVersion}, this host implements {ApiVersion}");
        }

        // A patch this extension does not own is one it could use to displace another's work, so
        // scoping is checked before the script is even read.
        foreach (string patch in manifest.Patches)
        {
            if (!patch.StartsWith(manifest.Id + ".", StringComparison.Ordinal))
            {
                return Reject(
                    manifest.Id,
                    SteamUiExtensionRejection.UnscopedPatch,
                    $"patch '{patch}' must start with '{manifest.Id}.'");
            }
        }

        if (!TryReadScript(directory, manifest.Script, out string? script, out string? scriptError))
        {
            return Reject(manifest.Id, SteamUiExtensionRejection.UnreadableScript, scriptError);
        }

        return new SteamUiExtension(
            manifest.Id, manifest, script, SteamUiExtensionRejection.None, null);
    }

    private static bool TryReadScript(
        string directory,
        string relative,
        out string? script,
        out string? error)
    {
        script = null;
        error = null;
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(directory, relative));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
            or PathTooLongException)
        {
            error = ex.Message;
            return false;
        }

        // The script must be inside the package. Without this a manifest could name
        // ..\..\anything and have the host read and inject a file it never installed.
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            error = "the script path leaves the package directory";
            return false;
        }

        try
        {
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                error = "the declared script does not exist";
                return false;
            }
            // Checked before reading rather than after, so an oversized file is never loaded.
            if (info.Length > MaximumScriptCharacters)
            {
                error = $"the script exceeds {MaximumScriptCharacters} characters";
                return false;
            }
            script = File.ReadAllText(full, new UTF8Encoding(false, true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or DecoderFallbackException)
        {
            error = ex.Message;
            return false;
        }

        if (script.Length > MaximumScriptCharacters)
        {
            script = null;
            error = $"the script exceeds {MaximumScriptCharacters} characters";
            return false;
        }

        return true;
    }

    private static SteamUiExtension Reject(
        string id,
        SteamUiExtensionRejection rejection,
        string? detail) => new(id, null, null, rejection, detail);

    private static bool IsSafeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 96)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!(character is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '.' or '-' or '_'))
            {
                return false;
            }
        }

        // A leading or trailing separator makes the patch-scope prefix ambiguous.
        return value[0] is not ('.' or '-' or '_')
            && value[^1] is not ('.' or '-' or '_');
    }
}
