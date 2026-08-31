using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>What an installed extension declares about itself.</summary>
/// <remarks>
/// Deliberately small. Everything here is either needed to decide whether the extension can be
/// loaded at all, or is shown to the user; anything an extension wants to say about what it does
/// belongs in its own code, not in a manifest the host has to keep understanding.
/// </remarks>
public sealed class SteamUiExtensionManifest
{
    /// <summary>Stable identifier, unique across installed extensions.</summary>
    /// <remarks>Also the prefix every patch the extension owns must use, so an extension cannot
    /// name a patch that collides with the host's or another extension's.</remarks>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Name shown to the user.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>The extension's own version.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>The extension API version this was built against.</summary>
    /// <remarks>Checked for exact equality, not compatibility. The injected surface is coupled to a
    /// Steam build and to this host's bridge; an extension built against a different contract has
    /// no way to degrade gracefully, so refusing it with a clear reason is the honest outcome.</remarks>
    [JsonPropertyName("apiVersion")]
    public int ApiVersion { get; set; }

    /// <summary>The JavaScript file, relative to the package root, injected with the asset.</summary>
    [JsonPropertyName("script")]
    public string Script { get; set; } = string.Empty;

    /// <summary>The patch identifiers this extension installs and removes.</summary>
    /// <remarks>Declared rather than discovered so the host can refuse a collision before any
    /// extension code runs, and so an extension that fails to load still has its patches removed.</remarks>
    [JsonPropertyName("patches")]
    public List<string> Patches { get; set; } = [];
}

/// <summary>Why an extension was not loaded.</summary>
public enum SteamUiExtensionRejection
{
    /// <summary>The extension loaded.</summary>
    None,

    /// <summary>The manifest was missing, unreadable, or not valid JSON.</summary>
    UnreadableManifest,

    /// <summary>A required field was empty, or the id is not a safe identifier.</summary>
    InvalidManifest,

    /// <summary>Built against a different extension API version.</summary>
    ApiVersionMismatch,

    /// <summary>The declared script is missing, outside the package, or too large.</summary>
    UnreadableScript,

    /// <summary>A patch id is not prefixed with the extension's own id.</summary>
    UnscopedPatch,

    /// <summary>Another installed extension already claimed this id or one of its patches.</summary>
    Conflict,
}

/// <summary>One extension the host examined, loaded or not.</summary>
/// <param name="Id">The extension's declared id, or its directory name when the manifest could not
/// be read — so a rejected extension can still be named in a log and in the UI.</param>
/// <param name="Manifest">The manifest, when it parsed.</param>
/// <param name="Script">The JavaScript to inject, when it was readable.</param>
/// <param name="Rejection">Why it was not loaded, or <see cref="SteamUiExtensionRejection.None"/>.</param>
/// <param name="Detail">The specific reason, for the log and for the user.</param>
public sealed record SteamUiExtension(
    string Id,
    SteamUiExtensionManifest? Manifest,
    string? Script,
    SteamUiExtensionRejection Rejection,
    string? Detail)
{
    /// <summary>Whether this extension contributes to the session.</summary>
    public bool Loaded => Rejection == SteamUiExtensionRejection.None;
}
