using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WSGM.Core;

/// <summary>One theme folder under the themes root, with its saved state.</summary>
/// <remarks>
///     Mirrors <c>Theme</c> in <c>css_theme.py</c> (b1bc683). The saved state lives beside the theme
///     in <c>config_USER.json</c>, the file CSS Loader writes for a user named <c>user</c>, so a
///     themes folder copied from a Deck keeps which themes were on and what their patches were set to.
///     A <c>PRIORITY</c> file holding a number moves the theme later in the cascade.
/// </remarks>
public sealed class InstalledTheme
{
    /// <summary>The saved-state file's name, as CSS Loader names it for the default user.</summary>
    public const string ConfigFileName = "config_USER.json";

    /// <summary>Creates the theme from a folder.</summary>
    /// <param name="themePath">The folder's full path.</param>
    /// <param name="manifest">Its manifest, parsed or generated.</param>
    /// <param name="themesRoot">The themes folder, which images are relative to.</param>
    public InstalledTheme(string themePath, ThemeManifestData manifest, string themesRoot)
    {
        ThemePath = themePath;
        ThemesRoot = themesRoot;
        ConfigPath = Path.Combine(themePath, ConfigFileName);
        Name = manifest.Name;
        DisplayName = manifest.DisplayName;
        Id = manifest.Id;
        Version = manifest.Version;
        Author = manifest.Author;
        Require = manifest.Require;
        Flags = manifest.Flags;
        TabMappings = manifest.TabMappings;
        Dependencies = manifest.Dependencies;
        try
        {
            var priority = Path.Combine(themePath, "PRIORITY");
            if (File.Exists(priority))
            {
                var line = File.ReadLines(priority).FirstOrDefault()?.Trim();
                if (int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    PriorityMod = value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A PRIORITY file that cannot be read is no priority, as in CSS Loader.
        }

        var manifestPath = Path.Combine(themePath, ThemeManifest.FileName);
        Created = File.Exists(manifestPath) ? File.GetLastWriteTimeUtc(manifestPath) : null;
        Modified = File.Exists(ConfigPath) ? File.GetLastWriteTimeUtc(ConfigPath) : null;
        Injects = [.. manifest.Injects.Select(inject => CreateInject(inject))];
        Patches = [.. manifest.Patches.Select(patch => new ThemePatch(patch, this))];
    }

    /// <summary>The folder.</summary>
    public string ThemePath { get; }

    /// <summary>The themes root.</summary>
    public string ThemesRoot { get; }

    /// <summary>The saved-state file.</summary>
    public string ConfigPath { get; }

    /// <summary>The theme's identity among installed themes; suffixed by the loader on a clash.</summary>
    public string Name { get; private set; }

    /// <summary>What the user sees, or null to show the name.</summary>
    public string? DisplayName { get; private set; }

    /// <summary>The store id, or the name.</summary>
    public string Id { get; }

    /// <summary>The version text.</summary>
    public string Version { get; }

    /// <summary>The author text.</summary>
    public string Author { get; }

    /// <summary>The manifest version the theme was written for.</summary>
    public int Require { get; }

    /// <summary>The flags, upper-cased.</summary>
    public IReadOnlyList<string> Flags { get; }

    /// <summary>The theme's own tab aliases.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TabMappings { get; }

    /// <summary>Themes this one needs, each with the patch values it sets on them.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonNode>> Dependencies { get; }

    /// <summary>What the theme always injects while enabled.</summary>
    public IReadOnlyList<ThemeInject> Injects { get; }

    /// <summary>The theme's patches.</summary>
    public IReadOnlyList<ThemePatch> Patches { get; }

    /// <summary>The number in the PRIORITY file, or zero.</summary>
    public int PriorityMod { get; }

    /// <summary>When the manifest was last written, or null without one.</summary>
    public DateTime? Created { get; }

    /// <summary>When the saved state was last written, or null without any.</summary>
    public DateTime? Modified { get; private set; }

    /// <summary>Whether the theme is on.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Whether the theme is a profile.</summary>
    public bool IsPreset => Flags.Contains(ThemeFlags.Preset);

    /// <summary>The name the user sees.</summary>
    public string EffectiveDisplayName => DisplayName ?? Name;

    /// <summary>Builds a block from a manifest entry, expanding its tabs through the theme's aliases.</summary>
    /// <param name="inject">The manifest entry.</param>
    /// <param name="scope">The patch and option the entry is under, or empty for the theme's own.</param>
    /// <returns>The block.</returns>
    public ThemeInject CreateInject(ThemeInjectManifest inject, string scope = "")
    {
        var identity = $"{Name}|{scope}|{inject.Key}";
        if (inject.Key.StartsWith("--", StringComparison.Ordinal))
        {
            var value = inject.Tabs.Count > 0 ? inject.Tabs[0] : string.Empty;
            var tabs = inject.Tabs.Count > 1 ? inject.Tabs.Skip(1).ToList() : [];
            return new ThemeInject(
                ThemeTargets.Expand(tabs, TabMappings),
                $":root {{ {inject.Key}: {value}; }}",
                identity);
        }

        return new ThemeInject(
            Path.Combine(ThemePath, inject.Key.Replace('/', Path.DirectorySeparatorChar)),
            ThemeTargets.Expand(inject.Tabs, TabMappings),
            identity);
    }

    /// <summary>Reads the saved state and applies it: whether the theme is on and each patch's value.</summary>
    /// <returns>Null, or why the file could not be read.</returns>
    public string? LoadConfig()
    {
        if (!File.Exists(ConfigPath))
        {
            return null;
        }

        JsonObject? config;
        try
        {
            config = JsonNode.Parse(File.ReadAllText(ConfigPath)) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ex.Message;
        }

        if (config is null)
        {
            return "config is not an object";
        }

        var activate = false;
        foreach (var (key, value) in config)
        {
            if (key == "active")
            {
                activate = value is JsonValue flag && flag.TryGetValue(out bool on) && on;
                continue;
            }

            foreach (var patch in Patches.Where(patch => patch.Name == key))
            {
                patch.SetSavedValue(value);
            }
        }

        if (activate)
        {
            Enable(false);
        }

        return null;
    }

    /// <summary>Writes the saved state.</summary>
    /// <returns>Null, or why it could not be written.</returns>
    public string? SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(ThemePath);
            JsonObject config = new() { ["active"] = Enabled };
            foreach (var patch in Patches)
            {
                config[patch.Name] = patch.SavedValue();
            }

            AtomicFile.WriteText(ConfigPath, config.ToJsonString(), false);
            Modified = DateTime.UtcNow;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>Turns the theme on: its blocks and each patch's chosen blocks.</summary>
    /// <param name="save">Whether the state is saved, as CSS Loader saves it on every inject; loading it is not a change.</param>
    /// <returns>Null, or why the saved state could not be written.</returns>
    public string? Enable(bool save = true)
    {
        foreach (var inject in Injects)
        {
            inject.Enabled = true;
        }

        foreach (var patch in Patches)
        {
            patch.Apply();
        }

        Enabled = true;
        return save ? SaveConfig() : null;
    }

    /// <summary>Turns the theme off.</summary>
    /// <returns>Null, or why the saved state could not be written.</returns>
    public string? Disable()
    {
        foreach (var inject in AllInjects())
        {
            inject.Enabled = false;
        }

        Enabled = false;
        return SaveConfig();
    }

    /// <summary>Turns the theme off and removes its folder.</summary>
    /// <returns>Null, or why the folder could not be removed.</returns>
    public string? Delete()
    {
        // The folder goes next, so a state that could not be saved does not matter.
        _ = Disable();
        try
        {
            Directory.Delete(ThemePath, true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>Every block the theme owns: its own, then each patch's, in order.</summary>
    /// <returns>Live injection objects in application order; enumerating does not enable them.</returns>
    public IEnumerable<ThemeInject> AllInjects()
    {
        foreach (var inject in Injects)
        {
            yield return inject;
        }

        foreach (var patch in Patches)
        {
            foreach (var inject in patch.Injects)
            {
                yield return inject;
            }
        }
    }

    /// <summary>Renames the theme when another already has its name, as CSS Loader does.</summary>
    /// <param name="suffix">The number appended.</param>
    public void AddPrefix(int suffix)
    {
        DisplayName ??= Name;
        Name += $"_{suffix}";
    }

    /// <summary>The snapshot the UI reads.</summary>
    /// <returns>A presentation snapshot containing the theme and its current patch selections.</returns>
    public ThemeSnapshot Snapshot()
    {
        return new ThemeSnapshot(
            Id,
            Name,
            EffectiveDisplayName,
            Version,
            Author,
            Enabled,
            [.. Patches.Select(patch => patch.Snapshot())],
            Require,
            [.. Dependencies.Keys],
            Flags,
            Created,
            Modified);
    }
}

/// <summary>What the UI shows of an installed theme.</summary>
/// <param name="Id">The store id, or the name.</param>
/// <param name="Name">The identity among installed themes.</param>
/// <param name="DisplayName">The name the user sees.</param>
/// <param name="Version">The version text.</param>
/// <param name="Author">The author text.</param>
/// <param name="Enabled">Whether it is on.</param>
/// <param name="Patches">Its patches.</param>
/// <param name="Require">The manifest version it was written for.</param>
/// <param name="Dependencies">The names of the themes it needs.</param>
/// <param name="Flags">Its flags.</param>
/// <param name="Created">When its manifest was last written.</param>
/// <param name="Modified">When its saved state was last written.</param>
public sealed record ThemeSnapshot(
    string Id,
    string Name,
    string DisplayName,
    string Version,
    string Author,
    bool Enabled,
    IReadOnlyList<ThemePatchSnapshot> Patches,
    int Require,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Flags,
    DateTime? Created,
    DateTime? Modified)
{
    /// <summary>Whether the theme is a profile.</summary>
    public bool IsPreset => Flags.Contains(ThemeFlags.Preset);
}
