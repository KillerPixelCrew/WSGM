using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What the store lists about a theme.</summary>
/// <param name="Id">The store id.</param>
/// <param name="Name">The theme's name, which is its identity once installed.</param>
/// <param name="DisplayName">What the store shows.</param>
/// <param name="Version">The version text.</param>
/// <param name="Target">The part of Steam the theme restyles.</param>
/// <param name="Targets">Every target the store tags it with.</param>
/// <param name="ManifestVersion">The manifest version it needs.</param>
/// <param name="SpecifiedAuthor">The author as the theme names them.</param>
/// <param name="AuthorName">The uploading account's name.</param>
/// <param name="ImageIds">The screenshots, as blob ids.</param>
/// <param name="DownloadId">The package, as a blob id, or null when the store lists none.</param>
/// <param name="DownloadCount">How often it was downloaded.</param>
/// <param name="StarCount">How often it was starred.</param>
/// <param name="Updated">When it was last updated, or null.</param>
public sealed record ThemeStoreSummary(
    string Id,
    string Name,
    string DisplayName,
    string Version,
    string Target,
    IReadOnlyList<string> Targets,
    int ManifestVersion,
    string SpecifiedAuthor,
    string AuthorName,
    IReadOnlyList<string> ImageIds,
    string? DownloadId,
    int DownloadCount,
    int StarCount,
    DateTimeOffset? Updated);
