using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using SteamUiToolkit;

namespace WSGM.Core;

/// <summary>The installed themes, their state, and the blocks that follow from it.</summary>
/// <remarks>
///     <para>
///         Mirrors <c>Loader</c> in <c>css_loader.py</c> (b1bc683): the folder scan and its
///         refusals, the score that orders the cascade (a PRIORITY file raises a theme, every theme
///         that depends on it lowers it), enabling a theme with its dependencies and the patch values
///         it sets on them, disabling one and the dependencies nothing else still uses, a profile as a
///         generated theme of dependencies, and the name suffix a duplicate gets.
///     </para>
///     <para>
///         Where CSS Loader appends and removes a block per window as state changes, this answers the
///         whole cascade on request, in the order CSS Loader would have injected it at load, and the
///         toolkit's gate brings every window in step. Not thread-safe: the service serializes calls.
///     </para>
/// </remarks>
public sealed class ThemeLoader
{
    private List<InstalledTheme> _cascade = [];
    private ThemeClassMappings _mappings;
    private Dictionary<string, int> _scores = new(StringComparer.Ordinal);
    private List<InstalledTheme> _themes = [];

    /// <summary>Creates the loader over a themes folder.</summary>
    /// <param name="root">The folder holding one subfolder per theme.</param>
    /// <param name="mappings">The class translations, or none.</param>
    public ThemeLoader(string root, ThemeClassMappings? mappings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = root;
        _mappings = mappings ?? ThemeClassMappings.Empty;
    }

    /// <summary>The themes folder.</summary>
    public string Root { get; }

    /// <summary>The installed themes, by display name.</summary>
    public IReadOnlyList<InstalledTheme> Themes => _themes;

    /// <summary>The folders the last load refused, each with why.</summary>
    public IReadOnlyList<ThemeLoadError> LastLoadErrors { get; private set; } = [];

    /// <summary>Replaces the class translations; every stylesheet is read again through them.</summary>
    /// <param name="mappings">The new table.</param>
    public void SetMappings(ThemeClassMappings mappings)
    {
        _mappings = mappings;
        foreach (var inject in _themes.SelectMany(theme => theme.AllInjects()))
        {
            inject.Invalidate();
        }
    }

    /// <summary>Reads the folder, orders the cascade and applies each theme's saved state.</summary>
    public void Load()
    {
        try
        {
            ThemeInstaller.Recover(Root);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or JsonException)
        {
            LastLoadErrors = [new ThemeLoadError(Root, "Theme update recovery remains pending: " + ex.Message)];
            return;
        }

        _themes = [];
        LastLoadErrors = ParseThemes();
        _scores = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var theme in _themes)
        {
            SetThemeScore(theme, []);
        }

        // A stable sort over the folder order, as Python's sort is: equal scores keep their order.
        _cascade = [.. _themes.OrderBy(theme => _scores[theme.Name])];
        foreach (var theme in _cascade)
        {
            if (theme.LoadConfig() is { } error)
            {
                Log.Warn($"Theme '{theme.Name}': saved state could not be read: {error}");
            }
        }

        _themes = [.. _themes.OrderBy(theme => theme.EffectiveDisplayName, StringComparer.Ordinal)];
    }

    /// <summary>The theme with a name, or null.</summary>
    /// <param name="name">The theme's name.</param>
    public InstalledTheme? Find(string name)
    {
        return _themes.FirstOrDefault(theme => theme.Name == name);
    }

    /// <summary>Turns a theme on or off, with CSS Loader's dependency rules.</summary>
    /// <param name="name">The theme's name.</param>
    /// <param name="enabled">Whether it should be on.</param>
    /// <returns>Null, or why not.</returns>
    public string? SetThemeState(string name, bool enabled)
    {
        var theme = Find(name);
        if (theme is null)
        {
            return $"Did not find theme {name}";
        }

        // The themes change either way; a state that could not be saved is reported.
        return enabled
            ? EnableTheme(theme, true, true, [])
            : DisableTheme(theme, theme.Flags.Contains(ThemeFlags.KeepDependencies), []);
    }

    /// <summary>Chooses a patch's option.</summary>
    /// <param name="themeName">The theme's name.</param>
    /// <param name="patchName">The patch's name.</param>
    /// <param name="value">The option.</param>
    /// <returns>Null, or why not.</returns>
    public string? SetPatch(string themeName, string patchName, string value)
    {
        var theme = Find(themeName);
        if (theme is null)
        {
            return $"Did not find theme '{themeName}'";
        }

        var patch = theme.Patches.FirstOrDefault(candidate => candidate.Name == patchName);
        if (patch is null)
        {
            return $"Did not find patch '{patchName}' for theme '{themeName}'";
        }

        if (patch.Value == value)
        {
            return null;
        }

        patch.TrySetValue(value);
        if (theme.Enabled)
        {
            patch.Apply();
        }

        return theme.SaveConfig();
    }

    /// <summary>Sets a component's colour or image.</summary>
    /// <param name="themeName">The theme's name.</param>
    /// <param name="patchName">The patch's name.</param>
    /// <param name="componentName">The component's name.</param>
    /// <param name="value">The colour or image path.</param>
    /// <returns>Null, or why not.</returns>
    public string? SetComponent(string themeName, string patchName, string componentName, string value)
    {
        var theme = Find(themeName);
        if (theme is null)
        {
            return $"Did not find theme '{themeName}'";
        }

        var patch = theme.Patches.FirstOrDefault(candidate => candidate.Name == patchName);
        if (patch is null)
        {
            return $"Did not find patch '{patchName}' for theme '{themeName}'";
        }

        var component = patch.Components.FirstOrDefault(candidate => candidate.Name == componentName);
        if (component is null)
        {
            return $"Failed to find component '{componentName}'";
        }

        component.Value = value;
        if (component.Generate() is { } refusal)
        {
            return refusal;
        }

        return theme.SaveConfig();
    }

    /// <summary>Turns a theme off and removes its folder.</summary>
    /// <param name="name">The theme's name.</param>
    /// <returns>Null, or why not.</returns>
    public string? DeleteTheme(string name)
    {
        var theme = Find(name);
        if (theme is null)
        {
            return $"Could not find theme {name}";
        }

        if (theme.Delete() is { } error)
        {
            return error;
        }

        _themes.Remove(theme);
        _cascade.Remove(theme);
        return null;
    }

    /// <summary>Writes a profile of every enabled theme and its patch values.</summary>
    /// <param name="name">The profile's name.</param>
    /// <returns>Null, or why not.</returns>
    /// <remarks>The caller reloads afterwards; the folder holds a theme the loader has not read.</remarks>
    public string? GeneratePreset(string name)
    {
        JsonObject dependenciesJson = [];
        foreach (var theme in _themes.Where(theme => theme.Enabled && !theme.IsPreset))
        {
            JsonObject values = [];
            foreach (var patch in theme.Patches)
            {
                values[patch.Name] = patch.SavedValue();
            }

            dependenciesJson[theme.Name] = values;
        }

        var displayName = name.EndsWith(".profile", StringComparison.Ordinal) ? name[..^8] : name;
        if (string.IsNullOrWhiteSpace(displayName) || displayName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "The profile name is not a valid folder name.";
        }

        name = displayName + ".profile";
        var existing = Find(name);
        if (existing is not null && !existing.IsPreset)
        {
            return $"Theme '{name}' already exists";
        }

        if (existing is null)
        {
            existing = Find(displayName);
            if (existing is not null)
            {
                if (existing.IsPreset)
                {
                    name = existing.Name;
                }
                else
                {
                    return $"Theme '{displayName}' already exists";
                }
            }
        }

        var themePath = Path.Combine(Root, name);
        try
        {
            Directory.CreateDirectory(themePath);
            JsonObject manifest = new()
            {
                ["display_name"] = displayName,
                ["name"] = name,
                ["manifest_version"] = ThemeManifest.SupportedVersion,
                ["flags"] = new JsonArray(ThemeFlags.Preset),
                ["dependencies"] = dependenciesJson
            };
            AtomicFile.WriteText(Path.Combine(themePath, ThemeManifest.FileName), manifest.ToJsonString(), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        return null;
    }

    /// <summary>Every enabled block in cascade order, as the toolkit installs it.</summary>
    /// <returns>The blocks; a stylesheet that cannot be read is left out and its error logged once.</returns>
    public IReadOnlyList<SteamThemeStyle> ActiveStyles()
    {
        List<SteamThemeStyle> styles = [];
        foreach (var theme in _cascade)
        {
            if (!theme.Enabled)
            {
                continue;
            }

            foreach (var inject in theme.AllInjects())
            {
                if (!inject.Enabled || inject.Targets.Count == 0)
                {
                    continue;
                }

                var css = inject.Load(_mappings);
                if (css is null)
                {
                    Log.Change(
                        $"theme.inject.{inject.Id}",
                        $"Theme '{theme.Name}': {inject.CssPath} could not be read: {inject.LoadError}",
                        LogLevel.Warn);
                    continue;
                }

                if (css.Length == 0)
                {
                    continue;
                }

                styles.Add(new SteamThemeStyle(inject.Id, css, inject.Targets, inject.Hash()));
            }
        }

        return styles;
    }

    /// <summary>Turns a theme on, and the dependencies it names, as CSS Loader does.</summary>
    /// <returns>Null, or the first saved state that could not be written.</returns>
    private string? EnableTheme(
        InstalledTheme theme,
        bool setDependencies,
        bool setDependencyValues,
        IReadOnlyCollection<string> ignore)
    {
        string? error = null;
        if (setDependencies)
        {
            List<string> dependencyNames = [.. theme.Dependencies.Keys];
            // The top level controls every dependency it names; one it names is not re-decided by a
            // dependency further down.
            List<string> ignoreNext = [.. ignore, .. dependencyNames];
            // A profile's dependencies do not override anything themselves.
            if (theme.IsPreset)
            {
                setDependencies = false;
            }

            // Higher priority themes sorted right, as CSS Loader sorts them.
            dependencyNames = [.. dependencyNames.OrderBy(name => _scores.GetValueOrDefault(name, 0))];
            foreach (var dependencyName in dependencyNames)
            {
                if (ignore.Contains(dependencyName))
                {
                    continue;
                }

                var dependency = Find(dependencyName);
                if (dependency is null)
                {
                    continue;
                }

                if (setDependencyValues)
                {
                    if (dependency.Enabled)
                    {
                        var disabled = dependency.Disable();
                        error ??= disabled;
                    }

                    foreach (var (patchName, patchValue) in theme.Dependencies[dependencyName])
                    {
                        foreach (var patch in dependency.Patches.Where(patch => patch.Name == patchName))
                        {
                            patch.SetSavedValue(patchValue);
                        }
                    }
                }

                var enabled = EnableTheme(dependency, setDependencies, setDependencyValues, ignoreNext);
                error ??= enabled;
            }
        }

        var own = theme.Enable();
        return error ?? own;
    }

    /// <summary>Turns a theme off, and each dependency nothing else still uses.</summary>
    /// <param name="theme">The theme.</param>
    /// <param name="keepDependencies">Whether its dependencies stay on.</param>
    /// <param name="visited">
    ///     The themes this call has turned off already. Dependencies may form a cycle, which CSS
    ///     Loader's own recursion never ends on; here each theme is visited once.
    /// </param>
    /// <returns>Null, or the first saved state that could not be written.</returns>
    private string? DisableTheme(InstalledTheme theme, bool keepDependencies, HashSet<string> visited)
    {
        if (!visited.Add(theme.Name))
        {
            return null;
        }

        var error = theme.Disable();
        if (keepDependencies)
        {
            return error;
        }

        foreach (var dependencyName in theme.Dependencies.Keys)
        {
            var dependency = Find(dependencyName);
            if (dependency is null)
            {
                continue;
            }

            var used = _themes.Any(other => other.Enabled && other.Dependencies.ContainsKey(dependency.Name));
            if (!used)
            {
                var disabled = DisableTheme(dependency, false, visited);
                error ??= disabled;
            }
        }

        return error;
    }

    /// <summary>Scores a theme and lowers each dependency's score, as CSS Loader orders the cascade.</summary>
    /// <param name="theme">The theme.</param>
    /// <param name="path">
    ///     The themes on the way to this one. A dependency already on the path closes a cycle and is
    ///     only lowered, not descended into again, so a cycle ends where CSS Loader's recursion would
    ///     not; without one the order is CSS Loader's.
    /// </param>
    private void SetThemeScore(InstalledTheme theme, HashSet<string> path)
    {
        _scores.TryAdd(theme.Name, theme.PriorityMod);
        if (!path.Add(theme.Name))
        {
            return;
        }

        foreach (var dependencyName in theme.Dependencies.Keys)
        {
            var dependency = Find(dependencyName);
            if (dependency is null)
            {
                continue;
            }

            SetThemeScore(dependency, path);
            _scores[dependency.Name] -= 1;
        }

        path.Remove(theme.Name);
    }

    private List<ThemeLoadError> ParseThemes()
    {
        List<ThemeLoadError> failures = [];
        if (!Directory.Exists(Root))
        {
            return failures;
        }

        IEnumerable<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(Root).OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add(new ThemeLoadError(Root, ex.Message));
            return failures;
        }

        foreach (var themePath in folders)
        {
            var folder = Path.GetFileName(themePath);
            try
            {
                var manifestPath = Path.Combine(themePath, ThemeManifest.FileName);
                ThemeManifestData manifest;
                if (File.Exists(manifestPath))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                    manifest = ThemeManifest.Parse(document.RootElement);
                }
                else if (File.Exists(Path.Combine(themePath, ThemeManifest.PlainStylesheet)))
                {
                    manifest = ThemeManifest.PlainStylesheetManifest(folder);
                }
                else
                {
                    throw new ThemeManifestException("Folder does not look like a theme?");
                }

                var theme = new InstalledTheme(themePath, manifest, Root);
                var names = _themes.Select(existing => existing.Name).ToHashSet(StringComparer.Ordinal);
                if (names.Contains(theme.Name))
                {
                    for (var suffix = 0; suffix < 5; suffix++)
                    {
                        if (!names.Contains($"{theme.Name}_{suffix}"))
                        {
                            theme.AddPrefix(suffix);
                            break;
                        }
                    }
                }

                if (names.Contains(theme.Name))
                {
                    // Five suffixed copies are CSS Loader's limit too; one more is reported, not dropped.
                    Log.Warn($"Theme folder '{folder}' refused: a theme named '{theme.Name}' is already loaded.");
                    failures.Add(new ThemeLoadError(folder, "A theme with this name is already loaded."));
                }
                else
                {
                    _themes.Add(theme);
                }
            }
            catch (Exception ex) when (ex is ThemeManifestException or JsonException or IOException
                                           or UnauthorizedAccessException or KeyNotFoundException)
            {
                Log.Warn($"Theme folder '{folder}' refused: {ex.Message}");
                failures.Add(new ThemeLoadError(folder, ex.Message));
            }
        }

        return failures;
    }
}

/// <summary>A folder the loader refused, and why.</summary>
/// <param name="Folder">The folder's name.</param>
/// <param name="Error">The reason.</param>
public sealed record ThemeLoadError(string Folder, string Error);
