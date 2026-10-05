using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Avalonia.Media;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Shell;

internal enum PhysicalGlyphSurface
{
    DeviceDescription,
    NavigationHint
}

/// <summary>
///     Path-free adapter from an imported physical profile to Avalonia-safe geometry plans.
/// </summary>
/// <remarks>
///     It never opens a package file, parses SVG, or performs network work; it consumes only the normalized
///     model returned by the SDK's bounded package loader. One plan is kept per profile revision and control,
///     so the cache holds at most the catalog's profiles times their controls and is cleared when the
///     catalog changes.
/// </remarks>
internal sealed class PhysicalGlyphPlans : IDisposable
{
    private readonly Dictionary<(string ProfileId, int Revision, GlyphControlId Control), PhysicalGlyphRenderPlan>
        _cache = [];

    private readonly PhysicalGlyphCatalog _catalog;
    private readonly Lock _gate = new();

    internal PhysicalGlyphPlans(PhysicalGlyphCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _catalog.Changed += ResetCache;
    }

    internal int CachedEntryCount
    {
        get
        {
            lock (_gate)
            {
                return _cache.Count;
            }
        }
    }

    public void Dispose()
    {
        _catalog.Changed -= ResetCache;
        ResetCache();
    }

    internal PhysicalGlyphRenderPlan Resolve(
        PhysicalGlyphSelectionResult selection,
        GlyphControlId requestedControl,
        PhysicalGlyphSurface surface,
        bool activeInputSourceIsManagedHandheld)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Profile is null)
        {
            return FallbackPlan(selection.FallbackReason);
        }

        var authorized = surface switch
        {
            PhysicalGlyphSurface.DeviceDescription => true,
            PhysicalGlyphSurface.NavigationHint => activeInputSourceIsManagedHandheld,
            _ => false
        };
        if (!authorized)
        {
            return FallbackPlan(PhysicalGlyphFallbackReason.SourceNotHandheld);
        }

        var key = (selection.Profile.Manifest.ProfileId, selection.Profile.Manifest.Revision, requestedControl);
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out var plan))
            {
                plan = BuildPlan(selection.Profile, requestedControl);
                _cache.Add(key, plan);
            }

            return plan;
        }
    }

    private void ResetCache()
    {
        lock (_gate)
        {
            _cache.Clear();
        }
    }

    private static PhysicalGlyphRenderPlan BuildPlan(
        ImportedGlyphProfile profile,
        GlyphControlId requestedControl)
    {
        var physicalControl = requestedControl;
        var alias = profile.Manifest.Aliases.FirstOrDefault(item => item.LogicalControl == requestedControl);
        if (alias is not null)
        {
            physicalControl = alias.PhysicalControl;
        }

        var mapping = profile.Manifest.Controls.FirstOrDefault(item => item.Control == physicalControl);
        if (mapping is null || mapping.Presence is GlyphControlPresence.Absent)
        {
            return FallbackPlan(
                mapping is null
                    ? PhysicalGlyphFallbackReason.ArtworkMissing
                    : PhysicalGlyphFallbackReason.ControlAbsent,
                physicalControl);
        }

        if (mapping.AssetId is not { } assetId
            || !profile.Assets.TryGetValue(assetId, out var asset))
        {
            return FallbackPlan(
                PhysicalGlyphFallbackReason.ArtworkMissing,
                physicalControl);
        }

        if (asset.Vector is not { } vector)
        {
            return new PhysicalGlyphRenderPlan
            {
                PhysicalControl = physicalControl,
                FallbackReason = PhysicalGlyphFallbackReason.None,
                ViewBox = null,
                Paths = [],
                RasterPng = asset.RasterPng
            };
        }

        try
        {
            var paths = vector.Paths.Select(path => new PhysicalGlyphPath(
                StreamGeometry.Parse(FillRulePrefix(path.FillRule) + ToAvaloniaPathData(path.Data)),
                path.Fill,
                path.Stroke,
                path.StrokeWidth,
                path.StrokeLineCap,
                path.StrokeLineJoin)).ToArray();
            return new PhysicalGlyphRenderPlan
            {
                PhysicalControl = physicalControl,
                FallbackReason = PhysicalGlyphFallbackReason.None,
                ViewBox = vector.ViewBox,
                Paths = paths,
                RasterPng = default
            };
        }
        catch (Exception)
        {
            // The importer accepted only its strict path grammar, but Avalonia is the final
            // renderer authority. A parser-version disagreement is a bounded fallback, never a
            // reason to expose source SVG bytes or take down the overlay.
            return FallbackPlan(
                PhysicalGlyphFallbackReason.RenderRejected,
                physicalControl);
        }
    }

    // Avalonia's path grammar defaults to even-odd filling, while SVG defaults to non-zero; the
    // leading fill-rule command keeps the artwork's own rule.
    private static string FillRulePrefix(string fillRule)
    {
        return string.Equals(fillRule, "evenodd", StringComparison.Ordinal) ? "F0 " : "F1 ";
    }

    private static string ToAvaloniaPathData(string normalized)
    {
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        StringBuilder output = new(normalized.Length + 16);
        var index = 0;
        while (index < tokens.Length)
        {
            var command = tokens[index++];
            if (output.Length > 0)
            {
                output.Append(' ');
            }

            output.Append(command);
            var arity = char.ToUpperInvariant(command[0]) switch
            {
                'M' or 'L' or 'T' => 2,
                'H' or 'V' => 1,
                'C' => 6,
                'S' or 'Q' => 4,
                'A' => 7,
                'Z' => 0,
                _ => throw new FormatException("Imported glyph path has an unsupported command.")
            };
            while (arity > 0 && index < tokens.Length && !char.IsAsciiLetter(tokens[index][0]))
            {
                if (index + arity > tokens.Length)
                {
                    throw new FormatException("Imported glyph path has an incomplete command.");
                }

                output.Append(' ');
                switch (arity)
                {
                    case 1:
                        output.Append(tokens[index]);
                        break;
                    case 7:
                        output.Append(tokens[index]).Append(',').Append(tokens[index + 1])
                            .Append(' ').Append(tokens[index + 2])
                            .Append(' ').Append(tokens[index + 3])
                            .Append(' ').Append(tokens[index + 4])
                            .Append(' ').Append(tokens[index + 5]).Append(',').Append(tokens[index + 6]);
                        break;
                    default:
                    {
                        for (var parameter = 0; parameter < arity; parameter += 2)
                        {
                            if (parameter > 0)
                            {
                                output.Append(' ');
                            }

                            output.Append(tokens[index + parameter]).Append(',')
                                .Append(tokens[index + parameter + 1]);
                        }

                        break;
                    }
                }

                index += arity;
            }
        }

        return output.ToString();
    }

    private static PhysicalGlyphRenderPlan FallbackPlan(
        PhysicalGlyphFallbackReason reason,
        GlyphControlId? physicalControl = null)
    {
        return new PhysicalGlyphRenderPlan
        {
            PhysicalControl = physicalControl,
            FallbackReason = reason,
            ViewBox = null,
            Paths = [],
            RasterPng = default
        };
    }
}
